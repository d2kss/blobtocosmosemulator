using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface IBlobStorageService
{
    Task<bool> MoveBlobToArchiveAsync(string sourceContainerName, string blobName, string? archiveContainerName = null);
}

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger<BlobStorageService> _logger;
    private readonly IConfiguration _configuration;

    public BlobStorageService(
        IConfiguration configuration,
        ILogger<BlobStorageService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        // Get connection string from configuration
        var connectionString = configuration["AzureWebJobsStorage"];
        
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("AzureWebJobsStorage connection string must be configured");
        }

        // Use connection string (supports both full connection string and UseDevelopmentStorage=true)
        _blobServiceClient = new BlobServiceClient(connectionString);
        _logger.LogInformation("Using connection string authentication for blob storage");
    }

    /// <summary>
    /// Move blob from source container to archive container (copy then delete from source).
    /// </summary>
    public async Task<bool> MoveBlobToArchiveAsync(string sourceContainerName, string blobName, string? archiveContainerName = null)
    {
        try
        {
            var archiveContainer = archiveContainerName
                ?? _configuration["BlobArchiveContainerName"]
                ?? _configuration["ArchiveContainerName"]
                ?? "archive";

            _logger.LogInformation("Moving blob to archive: {SourceContainer}/{BlobName} -> {ArchiveContainer}",
                sourceContainerName, blobName, archiveContainer);

            var sourceContainerClient = _blobServiceClient.GetBlobContainerClient(sourceContainerName);
            var archiveContainerClient = _blobServiceClient.GetBlobContainerClient(archiveContainer);

            await archiveContainerClient.CreateIfNotExistsAsync(PublicAccessType.None);

            var sourceBlobClient = sourceContainerClient.GetBlobClient(blobName);

            if (!await sourceBlobClient.ExistsAsync())
            {
                _logger.LogWarning("Blob does not exist: {ContainerName}/{BlobName}", sourceContainerName, blobName);
                return false;
            }

            var destBlobClient = archiveContainerClient.GetBlobClient(blobName);

            await destBlobClient.StartCopyFromUriAsync(sourceBlobClient.Uri);

            var copyComplete = await WaitForCopyCompletionAsync(destBlobClient);
            if (!copyComplete)
            {
                _logger.LogError("Copy to archive did not complete: {ArchiveContainer}/{BlobName}", archiveContainer, blobName);
                return false;
            }

            var deleted = await sourceBlobClient.DeleteIfExistsAsync();
            if (deleted.Value)
            {
                _logger.LogInformation("Successfully moved blob to archive: {SourceContainer}/{BlobName} -> {ArchiveContainer}",
                    sourceContainerName, blobName, archiveContainer);
                return true;
            }

            _logger.LogWarning("Blob copied to archive but delete from source failed: {ContainerName}/{BlobName}", sourceContainerName, blobName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving blob to archive: {SourceContainer}/{BlobName}", sourceContainerName, blobName);
            return false;
        }
    }

    private static async Task<bool> WaitForCopyCompletionAsync(BlobClient destBlobClient, int maxWaitSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var props = await destBlobClient.GetPropertiesAsync();
            var status = props.Value.CopyStatus;
            if (status == CopyStatus.Success)
                return true;
            if (status == CopyStatus.Failed)
                return false;
            await Task.Delay(1000);
        }
        return false;
    }
}
