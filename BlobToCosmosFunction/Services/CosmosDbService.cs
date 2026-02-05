using BlobToCosmosFunction.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface ICosmosDbService
{
    Task InitializeAsync();
    Task<bool> IsPhoneNumberExistsAsync(string normalizedNumber);
    Task<PhoneNumber?> SavePhoneNumberAsync(PhoneNumber phoneNumber, string sourceFile);
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
    /// Check if a phone number exists in Cosmos DB by normalized number.
    /// </summary>
    public async Task<bool> IsPhoneNumberExistsAsync(string normalizedNumber)
    {
        try
        {
            if (_container == null)
            {
                await InitializeAsync();
            }

            if (string.IsNullOrWhiteSpace(normalizedNumber))
            {
                return false;
            }

            // Query for existing phone number with this normalized number
            var query = new QueryDefinition("SELECT * FROM c WHERE c.NormalizedNumber = @normalizedNumber")
                .WithParameter("@normalizedNumber", normalizedNumber);

            var queryIterator = _container!.GetItemQueryIterator<PhoneNumber>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(normalizedNumber)
                });

            var results = await queryIterator.ReadNextAsync();
            return results.Any();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if phone number '{NormalizedNumber}' exists. Treating as new.", normalizedNumber);
            // On error, treat as new to be safe
            return false;
        }
    }

    /// <summary>
    /// Save a single phone number to Cosmos DB (line-by-line processing with delta detection).
    /// Checks if phone number exists, inserts if not found, and processes to Event Hub.
    /// </summary>
    public async Task<PhoneNumber?> SavePhoneNumberAsync(PhoneNumber phoneNumber, string sourceFile)
    {
        if (phoneNumber == null || string.IsNullOrWhiteSpace(phoneNumber.NormalizedNumber))
        {
            _logger.LogWarning("Invalid phone number provided: null or empty NormalizedNumber");
            return null;
        }

        try
        {
            // Ensure container is initialized
            if (_container == null)
            {
                await InitializeAsync();
            }

            // Step 1: Check if phone number already exists in Cosmos DB
            var exists = await IsPhoneNumberExistsAsync(phoneNumber.NormalizedNumber);
            
            PhoneNumber? savedPhoneNumber = null;
            bool wasInserted = false;

            if (exists)
            {
                _logger.LogDebug("Phone number '{NormalizedNumber}' already exists in Cosmos DB. Skipping insert.", 
                    phoneNumber.NormalizedNumber);
                
                // Optionally: Retrieve existing phone number if needed
                // For now, we'll still process to Event Hub even if it exists
            }
            else
            {
                // Step 2: Phone number does not exist - insert into Cosmos DB
                try
                {
                    // Set phone number properties before insertion
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

                    // Insert new phone number into Cosmos DB
                    var response = await _container!.CreateItemAsync(
                        item: phoneNumber,
                        partitionKey: new PartitionKey(phoneNumber.NormalizedNumber));

                    savedPhoneNumber = response.Resource;
                    wasInserted = true;
                    
                    _logger.LogInformation("Successfully inserted new phone number '{NormalizedNumber}' from source '{SourceFile}'", 
                        phoneNumber.NormalizedNumber, sourceFile);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // Race condition: Another process inserted the same phone number between check and insert
                    _logger.LogWarning("Phone number '{NormalizedNumber}' was inserted by another process (conflict). Treating as existing.", 
                        phoneNumber.NormalizedNumber);
                    // Continue to Event Hub processing even though insert failed due to conflict
                }
            }

            // Step 3: Process phone number to Event Hub (placeholder for future implementation)
            try
            {
                // TODO: Implement Event Hub processing
                // Example structure:
                // await _eventHubService.SendPhoneNumberAsync(phoneNumber, sourceFile, wasInserted);
                // 
                // This block should:
                // - Send phone number data to Event Hub
                // - Include metadata: sourceFile, wasInserted flag (indicates if this was a new insert), timestamp
                // - Handle Event Hub errors gracefully (log but don't fail the Cosmos DB operation)
                // 
                // Note: wasInserted = true means phone number was newly inserted, false means it already existed
                _ = wasInserted; // Placeholder - will be used in Event Hub implementation
                
                _logger.LogDebug("Event Hub processing placeholder for phone number '{NormalizedNumber}' (Inserted: {WasInserted})", 
                    phoneNumber.NormalizedNumber, wasInserted);
            }
            catch (Exception ex)
            {
                // Log Event Hub errors but don't fail the operation
                _logger.LogWarning(ex, "Error processing phone number '{NormalizedNumber}' to Event Hub. Cosmos DB operation succeeded.", 
                    phoneNumber.NormalizedNumber);
            }

            return savedPhoneNumber;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error saving phone number '{NormalizedNumber}' from source '{SourceFile}'. Status: {StatusCode}", 
                phoneNumber.NormalizedNumber, sourceFile, ex.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error saving phone number '{NormalizedNumber}' from source '{SourceFile}'", 
                phoneNumber.NormalizedNumber, sourceFile);
            return null;
        }
    }
}
