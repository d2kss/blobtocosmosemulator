using BlobToCosmosFunction.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface ICosmosDbService
{
    Task InitializeAsync();
    Task<List<PhoneNumber>> SavePhoneNumbersAsync(List<PhoneNumber> phoneNumbers, string sourceFile);
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
    /// Insert phone numbers into container.
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

            var saved = new List<PhoneNumber>();
            var failed = 0;

            foreach (var phoneNumber in phoneNumbers)
            {
                try
                {
                    phoneNumber.SourceFile = sourceFile;
                    var response = await _container!.UpsertItemAsync(
                        item: phoneNumber,
                        partitionKey: new PartitionKey(phoneNumber.NormalizedNumber));
                    saved.Add(response.Resource);
                }
                catch (CosmosException ex)
                {
                    failed++;
                    _logger.LogError(ex, "Failed to upsert phone number '{PhoneNumber}' from source '{SourceFile}'. Status: {StatusCode}", 
                        phoneNumber?.NormalizedNumber ?? "unknown", sourceFile, ex.StatusCode);
                    // Continue with next item instead of failing entire batch
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Unexpected error upserting phone number '{PhoneNumber}' from source '{SourceFile}'", 
                        phoneNumber?.NormalizedNumber ?? "unknown", sourceFile);
                    // Continue with next item instead of failing entire batch
                }
            }

            if (failed > 0)
            {
                _logger.LogWarning("Successfully inserted {SuccessCount} phone numbers, {FailedCount} failed from source '{SourceFile}'", 
                    saved.Count, failed, sourceFile);
            }
            else
            {
                _logger.LogInformation("Successfully inserted {Count} phone numbers from source '{SourceFile}'", saved.Count, sourceFile);
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
