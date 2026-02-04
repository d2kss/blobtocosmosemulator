using BlobToCosmosFunction.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface ICosmosDbService
{
    Task InitializeAsync();
    Task<List<PhoneNumber>> SavePhoneNumbersAsync(List<PhoneNumber> phoneNumbers, string sourceFile);
    Task<List<PhoneNumber>> GetNewPhoneNumbersAsync(List<PhoneNumber> phoneNumbers);
}

/// <summary>
/// Simple Cosmos DB service to create database and insert phone numbers.
/// </summary>
public class CosmosDbService : ICosmosDbService
{
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;
    private readonly string _containerName;
    private readonly ILogger<CosmosDbService> _logger;
    private Database? _database;
    private Container? _container;

    public CosmosDbService(
        IConfiguration configuration,
        ILogger<CosmosDbService> logger)
    {
        _logger = logger;

        try
        {
            // Get connection string
            var connectionString = configuration["CosmosDBConnection"]
                ?? configuration["ConnectionStrings:CosmosDB"]
                ?? throw new InvalidOperationException("Cosmos DB connection not configured.");

            _databaseName = configuration["CosmosDBDatabaseName"] ?? "BlobDataDB";
            _containerName = configuration["CosmosDBPhoneNumbersContainerName"] ?? "PhoneNumbers";

            // Build options with SSL bypass for emulator
            CosmosClientOptions options = new()
            {
                HttpClientFactory = () => new HttpClient(new HttpClientHandler()
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                }),
                ConnectionMode = ConnectionMode.Gateway
            };

            // Create CosmosClient
            _cosmosClient = new CosmosClient(connectionString, options);
            _logger.LogInformation("CosmosClient created successfully for database '{DatabaseName}'", _databaseName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create CosmosClient. Connection string configured: {HasConnectionString}", 
                !string.IsNullOrEmpty(configuration["CosmosDBConnection"]) || !string.IsNullOrEmpty(configuration["ConnectionStrings:CosmosDB"]));
            throw;
        }
    }

    /// <summary>
    /// Create database if it doesn't exist.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogInformation("Initializing database '{DatabaseName}' and container '{ContainerName}'", _databaseName, _containerName);
            
            _database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseName);
            _container = await _database.CreateContainerIfNotExistsAsync(
                id: _containerName,
                partitionKeyPath: "/NormalizedNumber");
            
            _logger.LogInformation("Database '{DatabaseName}' and container '{ContainerName}' ready", _databaseName, _containerName);
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error initializing database '{DatabaseName}' or container '{ContainerName}'. Status: {StatusCode}, Message: {Message}", 
                _databaseName, _containerName, ex.StatusCode, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error initializing database '{DatabaseName}' or container '{ContainerName}'", 
                _databaseName, _containerName);
            throw;
        }
    }

    /// <summary>
    /// Identify delta changes - get only new phone numbers that don't exist in Cosmos DB.
    /// Uses NormalizedNumber as the unique identifier to check for duplicates.
    /// </summary>
    public async Task<List<PhoneNumber>> GetNewPhoneNumbersAsync(List<PhoneNumber> phoneNumbers)
    {
        if (phoneNumbers == null || phoneNumbers.Count == 0)
        {
            return new List<PhoneNumber>();
        }

        try
        {
            if (_container == null)
            {
                await InitializeAsync();
            }

            var newPhoneNumbers = new List<PhoneNumber>();

            foreach (var phoneNumber in phoneNumbers)
            {
                try
                {
                    // Check if phone number already exists in Cosmos DB by querying with NormalizedNumber
                    // Since NormalizedNumber is the partition key, we can use it to check existence efficiently
                    var query = new QueryDefinition("SELECT * FROM c WHERE c.NormalizedNumber = @normalizedNumber")
                        .WithParameter("@normalizedNumber", phoneNumber.NormalizedNumber);

                    var queryIterator = _container!.GetItemQueryIterator<PhoneNumber>(
                        query,
                        requestOptions: new QueryRequestOptions
                        {
                            PartitionKey = new PartitionKey(phoneNumber.NormalizedNumber)
                        });

                    var results = await queryIterator.ReadNextAsync();

                    // If no results found, it's a new phone number (delta)
                    if (!results.Any())
                    {
                        newPhoneNumbers.Add(phoneNumber);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking if phone number '{PhoneNumber}' exists. Treating as new.", 
                        phoneNumber?.NormalizedNumber ?? "unknown");
                    // On error, treat as new to be safe
                    newPhoneNumbers.Add(phoneNumber);
                }
            }

            _logger.LogInformation("Identified {NewCount} new phone numbers out of {TotalCount} (delta changes)", 
                newPhoneNumbers.Count, phoneNumbers.Count);

            return newPhoneNumbers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error identifying new phone numbers");
            throw;
        }
    }

    /// <summary>
    /// Insert phone numbers into container (only new ones - delta changes).
    /// </summary>
    public async Task<List<PhoneNumber>> SavePhoneNumbersAsync(List<PhoneNumber> phoneNumbers, string sourceFile)
    {
        if (phoneNumbers == null || phoneNumbers.Count == 0)
        {
            _logger.LogWarning("No phone numbers provided to save from source file '{SourceFile}'", sourceFile);
            return new List<PhoneNumber>();
        }

        try
        {
            if (_container == null)
            {
                await InitializeAsync();
            }

            // Identify delta changes - only get new phone numbers
            var newPhoneNumbers = await GetNewPhoneNumbersAsync(phoneNumbers);
            
            if (newPhoneNumbers.Count == 0)
            {
                _logger.LogInformation("No new phone numbers to insert from source '{SourceFile}'. All are duplicates.", sourceFile);
                return new List<PhoneNumber>();
            }

            var saved = new List<PhoneNumber>();
            var failed = 0;

            // Insert only new phone numbers (delta changes)
            foreach (var phoneNumber in newPhoneNumbers)
            {
                try
                {
                    phoneNumber.SourceFile = sourceFile;
                    phoneNumber.FirstSeenAt = DateTime.UtcNow;
                    phoneNumber.LastSeenAt = DateTime.UtcNow;
                    phoneNumber.OccurrenceCount = 1;
                    
                    if (phoneNumber.SourceFiles == null)
                    {
                        phoneNumber.SourceFiles = new List<string>();
                    }
                    if (!phoneNumber.SourceFiles.Contains(sourceFile))
                    {
                        phoneNumber.SourceFiles.Add(sourceFile);
                    }

                    var response = await _container!.CreateItemAsync(
                        item: phoneNumber,
                        partitionKey: new PartitionKey(phoneNumber.NormalizedNumber));
                    saved.Add(response.Resource);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // Conflict - item was inserted by another process, skip
                    failed++;
                    _logger.LogWarning("Phone number '{PhoneNumber}' already exists (conflict). Skipping.", 
                        phoneNumber?.NormalizedNumber ?? "unknown");
                }
                catch (CosmosException ex)
                {
                    failed++;
                    _logger.LogError(ex, "Failed to insert phone number '{PhoneNumber}' from source '{SourceFile}'. Status: {StatusCode}", 
                        phoneNumber?.NormalizedNumber ?? "unknown", sourceFile, ex.StatusCode);
                    // Continue with next item instead of failing entire batch
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Unexpected error inserting phone number '{PhoneNumber}' from source '{SourceFile}'", 
                        phoneNumber?.NormalizedNumber ?? "unknown", sourceFile);
                    // Continue with next item instead of failing entire batch
                }
            }

            if (failed > 0)
            {
                _logger.LogWarning("Successfully inserted {SuccessCount} new phone numbers, {FailedCount} failed from source '{SourceFile}'", 
                    saved.Count, failed, sourceFile);
            }
            else
            {
                _logger.LogInformation("Successfully inserted {Count} new phone numbers (delta changes) from source '{SourceFile}'", 
                    saved.Count, sourceFile);
            }

            return saved;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error saving phone numbers from source '{SourceFile}'. Status: {StatusCode}, Message: {Message}", 
                sourceFile, ex.StatusCode, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error saving phone numbers from source '{SourceFile}'", sourceFile);
            throw;
        }
    }
}
