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
        var cancellationToken = context.CancellationToken;
        var blobName = name;
        _logger.LogInformation("=== BLOB TRIGGER FIRED ===");
        _logger.LogInformation("Blob trigger function processed blob: {Name} from container: input-files", blobName);
        
        const string containerName = "input-files";
        bool processingSuccessful = false;
        int processedCount = 0;
        int errorCount = 0;

        try
        {
            // Initialize CosmosDB if needed
            await _cosmosDbService.InitializeAsync(cancellationToken);

            _logger.LogInformation("Processing blob '{FileName}' line by line to reduce memory usage", blobName);

            // Process all lines
            await foreach (var line in _fileParserService.ReadLinesAsync(blobContent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                try
                {
                    var phoneNumber = _phoneNumberService.ExtractPhoneNumberFromLine(line, blobName);
                    if (phoneNumber != null)
                    {
                        await _cosmosDbService.SavePhoneNumberAsync(phoneNumber, blobName, cancellationToken);
                        processedCount++;
                    }
                }
                catch (Exception lineEx)
                {
                    errorCount++;
                    _logger.LogWarning(lineEx, "Error processing line in blob '{FileName}'. Continuing with next line.", blobName);
                    // Continue processing remaining lines even if one fails
                }
            }

            // Mark as successful only if no errors occurred during processing
            processingSuccessful = errorCount == 0;
            
            if (processingSuccessful)
            {
                _logger.LogInformation("Successfully processed blob '{FileName}'. Processed {Count} phone numbers.", blobName, processedCount);
            }
            else
            {
                _logger.LogWarning("Blob '{FileName}' processed with {ErrorCount} errors. Processed {ProcessedCount} phone numbers successfully. File will NOT be archived.", 
                    blobName, errorCount, processedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error processing blob: {Name}. File will NOT be archived.", blobName);
            // Don't archive blob if processing failed - allows for retry
            throw;
        }

        // Only archive if processing completed successfully (no errors)
        if (processingSuccessful)
        {
            try
            {
                var moved = await _blobStorageService.MoveBlobToArchiveAsync(containerName, blobName);
                if (moved)
                {
                    _logger.LogInformation("Successfully moved blob to archive after processing: {Name}", blobName);
                }
                else
                {
                    _logger.LogWarning("Blob was not moved to archive (may not exist or move failed): {Name}", blobName);
                }
            }
            catch (Exception archiveEx)
            {
                _logger.LogError(archiveEx, "Error archiving blob '{Name}' after successful processing. Blob remains in input container.", blobName);
                // Don't throw - processing was successful, archiving failure is non-critical
            }
        }
        else
        {
            _logger.LogInformation("Blob '{Name}' will remain in input container for retry due to processing errors.", blobName);
        }
    }
}
