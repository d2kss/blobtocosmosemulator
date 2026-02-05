using System.Text;
using BlobToCosmosFunction.Models;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface IFileParserService
{
    Task<FileData> ParseBlobContentAsync(Stream blobStream, string fileName);
    IAsyncEnumerable<string> ReadLinesAsync(Stream blobStream);
}

public class FileParserService : IFileParserService
{
    private readonly ILogger<FileParserService> _logger;

    public FileParserService(ILogger<FileParserService> logger)
    {
        _logger = logger;
    }

    public async Task<FileData> ParseBlobContentAsync(Stream blobStream, string fileName)
    {
        var fileData = new FileData
        {
            Id = Guid.NewGuid().ToString(),
            FileName = fileName,
            FileType = Path.GetExtension(fileName).ToLowerInvariant()
        };

        try
        {
            var recordCount = 0;
            var contentBuilder = new StringBuilder();

            await foreach (var line in ReadLinesAsync(blobStream))
            {
                recordCount++;
                contentBuilder.AppendLine(line);
            }

            fileData.Content = contentBuilder.ToString();
            fileData.RecordCount = recordCount;
            fileData.Status = "Processed";
            _logger.LogInformation("Successfully read file: {FileName}, Lines: {LineCount}", 
                fileName, fileData.RecordCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file: {FileName}", fileName);
            fileData.Status = "Error";
        }

        return fileData;
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(Stream blobStream)
    {
        using var reader = new StreamReader(blobStream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            yield return line;
        }
    }
}
