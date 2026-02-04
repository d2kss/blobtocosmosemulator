using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using BlobToCosmosFunction.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface ICosmosDbService
{
    Task InitializeAsync();
    Task<FileData> SaveFileDataAsync(FileData fileData);
    Task<List<PhoneNumber>> SavePhoneNumbersAsync(List<PhoneNumber> phoneNumbers, string sourceFile);
    Task<PhoneNumber?> GetPhoneNumberByNormalizedAsync(string normalizedNumber);
}

/// <summary>
/// Generic Cosmos DB service that works with both the local Cosmos DB emulator and Azure Cosmos DB.
/// Target is inferred from the connection string: localhost/127.0.0.1:8081 = emulator; otherwise Azure.
/// Use the same <c>CosmosDBConnection</c> in config for either environment.
/// </summary>
public class CosmosDbService : ICosmosDbService
{
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;
    private readonly string _fileDataContainerName;
    private readonly string _phoneNumbersContainerName;
    private readonly ILogger<CosmosDbService> _logger;
    private Database? _database;
    private Container? _fileDataContainer;
    private Container? _phoneNumbersContainer;

    public CosmosDbService(
        IConfiguration configuration,
        ILogger<CosmosDbService> logger)
    {
        _logger = logger;
        // Support both local.settings.json (CosmosDBConnection) and Aspire-injected config (ConnectionStrings:CosmosDB)
        var connectionString = configuration["CosmosDBConnection"]
            ?? configuration["ConnectionStrings:CosmosDB"]
            ?? configuration["CosmosDB:ConnectionString"]
            ?? configuration["COSMOSDB_CONNECTIONSTRING"]
            ?? throw new InvalidOperationException(
                "Cosmos DB connection not configured. Set CosmosDBConnection (or ConnectionStrings:CosmosDB when using Aspire).");

        _databaseName = configuration["CosmosDBDatabaseName"] ?? "BlobDataDB";
        _fileDataContainerName = configuration["CosmosDBContainerName"] ?? "ProcessedFiles";
        _phoneNumbersContainerName = configuration["CosmosDBPhoneNumbersContainerName"] ?? "PhoneNumbers";

        var isEmulator = IsEmulatorConnectionString(connectionString);
        connectionString = PrepareConnectionString(connectionString, isEmulator);
        var cosmosClientOptions = BuildCosmosClientOptions(isEmulator);

        _cosmosClient = new CosmosClient(connectionString, cosmosClientOptions);
        _logger.LogInformation(
            "CosmosDB (generic): {Mode}. Database: {DatabaseName}, Containers: {FileContainer}, {PhoneContainer}",
            isEmulator ? "Emulator" : "Azure",
            _databaseName, _fileDataContainerName, _phoneNumbersContainerName);
    }

    /// <summary>True if connection string points to local Cosmos DB emulator (localhost or 127.0.0.1:8081).</summary>
    private static bool IsEmulatorConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        return connectionString.Contains("localhost:8081", StringComparison.OrdinalIgnoreCase)
               || connectionString.Contains("127.0.0.1:8081", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Prepares connection string for the detected target (emulator vs Azure).</summary>
    private static string PrepareConnectionString(string connectionString, bool isEmulator)
    {
        if (!isEmulator)
            return connectionString;

        connectionString = connectionString
            .Replace("https://localhost:8081", "https://127.0.0.1:8081", StringComparison.OrdinalIgnoreCase)
            .Replace("https://localhost:8081/", "https://127.0.0.1:8081/", StringComparison.OrdinalIgnoreCase);
        if (!connectionString.Contains("DisableServerCertificateValidation", StringComparison.OrdinalIgnoreCase))
            connectionString = connectionString.TrimEnd(';') + ";DisableServerCertificateValidation=True;";
        return connectionString;
    }

    /// <summary>Builds CosmosClientOptions for either emulator (SSL bypass) or Azure (standard).</summary>
    private static CosmosClientOptions BuildCosmosClientOptions(bool isEmulator)
    {
        var options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            RequestTimeout = TimeSpan.FromSeconds(30),
            MaxRetryAttemptsOnRateLimitedRequests = 3,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30)
        };

        if (!isEmulator)
            return options;

        options.ServerCertificateCustomValidationCallback = (X509Certificate2 _, X509Chain _, SslPolicyErrors _) => true;
        options.HttpClientFactory = () =>
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };
            return new HttpClient(handler);
        };
        return options;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogInformation("Initializing CosmosDB database and container...");
            _logger.LogInformation("Connecting to CosmosDB at: {Endpoint}", _cosmosClient.Endpoint?.ToString() ?? "unknown");

            // Skip ReadAccountAsync (often fails with SSL on emulator). Create database directly with retry.
            const int maxRetries = 5;
            const int delayMs = 3000;
            Exception? lastEx = null;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseName);
                    _logger.LogInformation("Successfully connected to CosmosDB. Database: {DatabaseName}", _databaseName);
                    lastEx = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    _logger.LogWarning(ex, "CosmosDB init attempt {Attempt}/{Max} failed. Retrying in {Delay}ms...", attempt, maxRetries, delayMs);
                    if (attempt < maxRetries)
                        await Task.Delay(delayMs);
                }
            }
            if (lastEx != null || _database == null)
            {
                var ex = lastEx ?? new InvalidOperationException("Database creation returned null");
                _logger.LogError(ex, "Failed to connect to CosmosDB after {Max} attempts", maxRetries);
                var hint = (lastEx?.Message?.Contains("SSL", StringComparison.OrdinalIgnoreCase) == true ||
                            lastEx?.Message?.Contains("certificate", StringComparison.OrdinalIgnoreCase) == true)
                    ? " If SSL/certificate errors persist (e.g. corporate proxy or emulator), set \"UseLocalStorage\": \"true\" in local.settings.json to use local JSON storage instead."
                    : "";
                throw new InvalidOperationException($"Cannot connect to CosmosDB. Check connection string and that the service is reachable. Error: {ex.Message}.{hint}", ex);
            }

            // Create database already done above; continue with containers
            _logger.LogInformation("Database '{DatabaseName}' is ready", _databaseName);

            // Create FileData container if it doesn't exist
            var fileDataContainerProperties = new ContainerProperties(_fileDataContainerName, "/id")
            {
                IndexingPolicy = new IndexingPolicy
                {
                    Automatic = true,
                    IndexingMode = IndexingMode.Consistent,
                    IncludedPaths =
                    {
                        new IncludedPath { Path = "/" }  // Required root path
                    }
                }
            };

            _fileDataContainer = await _database.CreateContainerIfNotExistsAsync(fileDataContainerProperties);
            _logger.LogInformation("Container '{ContainerName}' is ready", _fileDataContainerName);

            // Create PhoneNumbers container if it doesn't exist
            // Use NormalizedNumber as partition key for efficient lookups
            var phoneNumbersContainerProperties = new ContainerProperties(_phoneNumbersContainerName, "/NormalizedNumber")
            {
                IndexingPolicy = new IndexingPolicy
                {
                    Automatic = true,
                    IndexingMode = IndexingMode.Consistent,
                    IncludedPaths =
                    {
                        new IncludedPath { Path = "/" },  // Required root path (must be first)
                        new IncludedPath { Path = "/NormalizedNumber/?" },
                        new IncludedPath { Path = "/Number/?" },
                        new IncludedPath { Path = "/SourceFile/?" }
                    }
                }
            };

            _phoneNumbersContainer = await _database.CreateContainerIfNotExistsAsync(phoneNumbersContainerProperties);
            _logger.LogInformation("Container '{ContainerName}' is ready", _phoneNumbersContainerName);
        }
        catch (CosmosException cosmosEx)
        {
            _logger.LogError(
                cosmosEx,
                "CosmosDB error initializing. StatusCode: {StatusCode}, SubStatusCode: {SubStatusCode}, Message: {Message}, ActivityId: {ActivityId}",
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode,
                cosmosEx.Message,
                cosmosEx.ActivityId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing CosmosDB: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<FileData> SaveFileDataAsync(FileData fileData)
    {
        if (_fileDataContainer == null)
        {
            await InitializeAsync();
        }

        try
        {
            // Ensure Id is set
            if (string.IsNullOrEmpty(fileData.Id))
            {
                fileData.Id = Guid.NewGuid().ToString();
                _logger.LogWarning("FileData.Id was empty, generated new Id: {Id}", fileData.Id);
            }

            _logger.LogInformation("Saving file data to CosmosDB: {FileName}, Id: {Id}", fileData.FileName, fileData.Id);

            var response = await _fileDataContainer!.CreateItemAsync(
                fileData,
                new PartitionKey(fileData.Id));

            _logger.LogInformation(
                "Successfully saved file data. File: {FileName}, RequestCharge: {RequestCharge}",
                fileData.FileName,
                response.RequestCharge);

            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning("Item with id {Id} already exists, updating instead", fileData.Id);
            var response = await _fileDataContainer!.UpsertItemAsync(
                fileData,
                new PartitionKey(fileData.Id));
            return response.Resource;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving file data to CosmosDB: {FileName}", fileData.FileName);
            throw;
        }
    }

    public async Task<List<PhoneNumber>> SavePhoneNumbersAsync(List<PhoneNumber> phoneNumbers, string sourceFile)
    {
        if (_phoneNumbersContainer == null)
        {
            await InitializeAsync();
        }

        var savedNumbers = new List<PhoneNumber>();
        var newCount = 0;
        var duplicateCount = 0;
        var updatedCount = 0;

        try
        {
            _logger.LogInformation("Processing {Count} phone numbers from {SourceFile}", phoneNumbers.Count, sourceFile);

            foreach (var phoneNumber in phoneNumbers)
            {
                try
                {
                    // Check if phone number already exists
                    var existing = await GetPhoneNumberByNormalizedAsync(phoneNumber.NormalizedNumber);

                    if (existing != null)
                    {
                        // Phone number exists - update it
                        existing.LastSeenAt = DateTime.UtcNow;
                        existing.OccurrenceCount++;
                        
                        // Add source file if not already in the list
                        if (!existing.SourceFiles.Contains(sourceFile))
                        {
                            existing.SourceFiles.Add(sourceFile);
                        }

                        var response = await _phoneNumbersContainer!.UpsertItemAsync(
                            existing,
                            new PartitionKey(existing.NormalizedNumber));

                        savedNumbers.Add(response.Resource);
                        duplicateCount++;
                        updatedCount++;
                        
                        _logger.LogDebug(
                            "Updated existing phone number: {Number} (seen {Count} times)",
                            phoneNumber.Number,
                            existing.OccurrenceCount);
                    }
                    else
                    {
                        // New phone number - insert it
                        var response = await _phoneNumbersContainer!.CreateItemAsync(
                            phoneNumber,
                            new PartitionKey(phoneNumber.NormalizedNumber));

                        savedNumbers.Add(response.Resource);
                        newCount++;
                        
                        _logger.LogDebug("Inserted new phone number: {Number}", phoneNumber.Number);
                    }
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    // Handle race condition - another process might have inserted it
                    _logger.LogWarning("Conflict inserting phone number {Number}, retrying lookup", phoneNumber.Number);
                    var existing = await GetPhoneNumberByNormalizedAsync(phoneNumber.NormalizedNumber);
                    if (existing != null)
                    {
                        existing.LastSeenAt = DateTime.UtcNow;
                        existing.OccurrenceCount++;
                        if (!existing.SourceFiles.Contains(sourceFile))
                        {
                            existing.SourceFiles.Add(sourceFile);
                        }
                        var response = await _phoneNumbersContainer!.UpsertItemAsync(
                            existing,
                            new PartitionKey(existing.NormalizedNumber));
                        savedNumbers.Add(response.Resource);
                        duplicateCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving phone number: {Number}", phoneNumber.Number);
                    // Continue with next phone number
                }
            }

            _logger.LogInformation(
                "Phone number processing completed. New: {NewCount}, Duplicates: {DuplicateCount}, Updated: {UpdatedCount}, Total: {TotalCount}",
                newCount,
                duplicateCount,
                updatedCount,
                savedNumbers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving phone numbers to CosmosDB");
            throw;
        }

        return savedNumbers;
    }

    public async Task<PhoneNumber?> GetPhoneNumberByNormalizedAsync(string normalizedNumber)
    {
        if (_phoneNumbersContainer == null)
        {
            await InitializeAsync();
        }

        try
        {
            var query = new QueryDefinition("SELECT * FROM c WHERE c.NormalizedNumber = @normalizedNumber")
                .WithParameter("@normalizedNumber", normalizedNumber);

            var iterator = _phoneNumbersContainer!.GetItemQueryIterator<PhoneNumber>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(normalizedNumber)
                });

            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                return response.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying phone number: {NormalizedNumber}", normalizedNumber);
        }

        return null;
    }
}
