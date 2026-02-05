using BlobToCosmosFunction.Models;
using BlobToCosmosFunction.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.IO;

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
        [BlobTrigger("input-files/{name}", Connection = "AzureWebJobsStorage")] Stream blobContent,
        string name,
        FunctionContext context)
    {
        var blobName = name;
        _logger.LogInformation("=== BLOB TRIGGER FIRED ===");
        _logger.LogInformation("Blob trigger function processed blob: {Name} from container: input-files", blobName);
        
        try
        {
            const string containerName = "input-files";

            // Initialize CosmosDB if needed
            await _cosmosDbService.InitializeAsync();

            _logger.LogInformation("Processing blob '{FileName}' line by line to reduce memory usage", blobName);

            await foreach (var line in _fileParserService.ReadLinesAsync(blobContent))
            {
                var phoneNumber = _phoneNumberService.ExtractPhoneNumberFromLine(line, blobName);
                if (phoneNumber != null)
                {
                    await _cosmosDbService.SavePhoneNumberAsync(phoneNumber, blobName);
                }
            }


            // Move the blob to archive container after successful processing (instead of deleting)
            var moved = await _blobStorageService.MoveBlobToArchiveAsync(containerName, blobName);
            if (moved)
            {
                _logger.LogInformation("Successfully moved blob to archive after processing: {Name}", blobName);
            }
            else
            {
                _logger.LogWarning("Blob was not moved to archive (may not exist or move failed): {Name}", blobName);
            }

            _logger.LogInformation("Successfully processed and moved blob to archive: {Name}", blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing blob: {Name}", blobName);
            // Don't delete blob if processing failed - allows for retry
            throw;
        }
    }
}
