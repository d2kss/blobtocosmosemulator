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
    }

    /// <summary>
    /// Create database if it doesn't exist.
    /// </summary>
    public async Task InitializeAsync()
    {
        _database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseName);
        _container = await _database.CreateContainerIfNotExistsAsync(
            id: _containerName,
            partitionKeyPath: "/NormalizedNumber");
        
        _logger.LogInformation("Database '{DatabaseName}' and container '{ContainerName}' ready", _databaseName, _containerName);
    }

    /// <summary>
    /// Insert phone numbers into container.
    /// </summary>
    public async Task<List<PhoneNumber>> SavePhoneNumbersAsync(List<PhoneNumber> phoneNumbers, string sourceFile)
    {
        if (_container == null)
        {
            await InitializeAsync();
        }

        var saved = new List<PhoneNumber>();
        foreach (var phoneNumber in phoneNumbers)
        {
            phoneNumber.SourceFile = sourceFile;
            var response = await _container!.UpsertItemAsync(
                item: phoneNumber,
                partitionKey: new PartitionKey(phoneNumber.NormalizedNumber));
            saved.Add(response.Resource);
        }

        _logger.LogInformation("Inserted {Count} phone numbers", saved.Count);
        return saved;
    }
}
