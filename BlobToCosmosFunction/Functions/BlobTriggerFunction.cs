using BlobToCosmosFunction.Models;
using BlobToCosmosFunction.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Functions;

public class BlobTriggerFunction
{
    private readonly IBlobStorageService _blobStorageService;
    private readonly IFileParserService _fileParserService;
    private readonly ICosmosDbService _cosmosDbService;
    private readonly IPhoneNumberService _phoneNumberService;
    private readonly ILogger<BlobTriggerFunction> _logger;

    public BlobTriggerFunction(
        IBlobStorageService blobStorageService,
        IFileParserService fileParserService,
        ICosmosDbService cosmosDbService,
        IPhoneNumberService phoneNumberService,
        ILogger<BlobTriggerFunction> logger)
    {
        _blobStorageService = blobStorageService;
        _fileParserService = fileParserService;
        _cosmosDbService = cosmosDbService;
        _phoneNumberService = phoneNumberService;
        _logger = logger;
    }

    [Function("BlobTriggerFunction")]
    public async Task Run(
        [BlobTrigger("input-files/{name}", Connection = "AzureWebJobsStorage")] byte[] blobContent,
        string name,
        FunctionContext context)
    {
        var blobName = name;
        _logger.LogInformation("=== BLOB TRIGGER FIRED ===");
        _logger.LogInformation("Blob trigger function processed blob: {Name} from container: input-files", blobName);
        _logger.LogInformation("Blob content size: {Size} bytes", blobContent?.Length ?? 0);

        try
        {
            const string containerName = "input-files";

            // Read blob using BlobStorageService (same approach as ReadBlobFunction)
            // This allows us to use SAS tokens or connection strings consistently
            using var blobStream = await _blobStorageService.ReadBlobAsync(containerName, blobName);

            // Parse the blob content
            var fileData = await _fileParserService.ParseBlobContentAsync(blobStream, blobName);

            // Initialize CosmosDB if needed
            await _cosmosDbService.InitializeAsync();

            // Extract phone numbers from blob content
            var phoneNumbers = _phoneNumberService.ExtractPhoneNumbers(fileData.Content, blobName);
            if (phoneNumbers.Any())
            {
                // Save phone numbers directly to Cosmos DB (identify delta changes - only insert new phone numbers)
                var savedPhoneNumbers = await _cosmosDbService.SavePhoneNumbersAsync(phoneNumbers, blobName);
                
               

                // TODO: Insert new phone numbers into API
                // The savedPhoneNumbers list contains only the new phone numbers (delta changes) that were inserted into Cosmos DB.
                // These phone numbers need to be sent to the external API for further processing.
                if (savedPhoneNumbers.Any())
                {
                    _logger.LogInformation("Found {Count} new phone numbers ready for API insertion", savedPhoneNumbers.Count);
                    
                    foreach (var phoneNumber in savedPhoneNumbers)
                    {
                        try
                        {
                            // TODO: Insert each new phone number into API
                            // Example: await _apiService.InsertPhoneNumberAsync(phoneNumber);
                            // The phoneNumber object contains all the details needed for API insertion:
                            // - phoneNumber.Number: The original phone number format
                            // - phoneNumber.NormalizedNumber: The normalized phone number (digits only)
                            // - phoneNumber.Id: Unique identifier
                            // - phoneNumber.SourceFile: Source file name
                            // - phoneNumber.FirstSeenAt: First seen timestamp
                            
                            _logger.LogDebug("Processing phone number '{PhoneNumber}' for API insertion", phoneNumber.NormalizedNumber);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing phone number '{PhoneNumber}' for API insertion", 
                                phoneNumber?.NormalizedNumber ?? "unknown");
                            // Continue with next phone number instead of failing entire batch
                        }
                    }
                    
                    _logger.LogInformation("Completed processing {Count} new phone numbers for API insertion", savedPhoneNumbers.Count);
                }
            }
            else
            {
                _logger.LogInformation("No phone numbers found in file: {Name}", blobName);
            }

            // Delete the blob after successful processing
            var deleted = await _blobStorageService.DeleteBlobAsync(containerName, blobName);
            if (deleted)
            {
                _logger.LogInformation("Successfully deleted blob after processing: {Name}", blobName);
            }
            else
            {
                _logger.LogWarning("Blob was already deleted or does not exist: {Name}", blobName);
            }

            _logger.LogInformation("Successfully processed and removed blob: {Name}", blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing blob: {Name}", blobName);
            // Don't delete blob if processing failed - allows for retry
            throw;
        }
    }
}
