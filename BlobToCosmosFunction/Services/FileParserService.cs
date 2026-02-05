using System.Text;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface IFileParserService
{
    IAsyncEnumerable<string> ReadLinesAsync(Stream blobStream);
}

public class FileParserService : IFileParserService
{
    private readonly ILogger<FileParserService> _logger;

    public FileParserService(ILogger<FileParserService> logger)
    {
        _logger = logger;
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
