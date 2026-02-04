using System.Net.Http.Json;
using System.Text.Json;
using BlobToCosmosFunction.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface IDncApiService
{
    Task<bool> RegisterDoNotCallAsync(PhoneNumber phoneNumber, string sourceFile);
    Task<List<bool>> RegisterDoNotCallBatchAsync(List<PhoneNumber> phoneNumbers, string sourceFile);
}

/// <summary>
/// Service to integrate with Do Not Call (DNC) API.
/// </summary>
public class DncApiService : IDncApiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    private readonly ILogger<DncApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public DncApiService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DncApiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Get API base URL from configuration
        _apiBaseUrl = configuration["DncApiBaseUrl"] 
            ?? configuration["DncApi:BaseUrl"]
            ?? "https://localhost:7242";

        // Set base address if not already set
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(_apiBaseUrl);
        }

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Register a single phone number with the DNC API.
    /// </summary>
    public async Task<bool> RegisterDoNotCallAsync(PhoneNumber phoneNumber, string sourceFile)
    {
        try
        {
            var request = MapToDncRequest(phoneNumber, sourceFile);
            var endpoint = "/marketing/dnc/doNotCall";

            _logger.LogDebug("Calling DNC API for phone number '{PhoneNumber}' from source '{SourceFile}'", 
                phoneNumber.NormalizedNumber, sourceFile);

            var response = await _httpClient.PostAsJsonAsync(endpoint, request, _jsonOptions);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully registered phone number '{PhoneNumber}' with DNC API", 
                    phoneNumber.NormalizedNumber);
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("DNC API returned status {StatusCode} for phone number '{PhoneNumber}'. Response: {Response}", 
                    response.StatusCode, phoneNumber.NormalizedNumber, errorContent);
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error calling DNC API for phone number '{PhoneNumber}' from source '{SourceFile}'", 
                phoneNumber?.NormalizedNumber ?? "unknown", sourceFile);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling DNC API for phone number '{PhoneNumber}' from source '{SourceFile}'", 
                phoneNumber?.NormalizedNumber ?? "unknown", sourceFile);
            return false;
        }
    }

    /// <summary>
    /// Register multiple phone numbers with the DNC API (batch processing).
    /// </summary>
    public async Task<List<bool>> RegisterDoNotCallBatchAsync(List<PhoneNumber> phoneNumbers, string sourceFile)
    {
        var results = new List<bool>();

        if (phoneNumbers == null || phoneNumbers.Count == 0)
        {
            _logger.LogWarning("No phone numbers provided for DNC API batch registration");
            return results;
        }

        _logger.LogInformation("Registering {Count} phone numbers with DNC API from source '{SourceFile}'", 
            phoneNumbers.Count, sourceFile);

        foreach (var phoneNumber in phoneNumbers)
        {
            var result = await RegisterDoNotCallAsync(phoneNumber, sourceFile);
            results.Add(result);
        }

        var successCount = results.Count(r => r);
        var failureCount = results.Count - successCount;

        if (failureCount > 0)
        {
            _logger.LogWarning("DNC API batch registration: {SuccessCount} succeeded, {FailureCount} failed from source '{SourceFile}'", 
                successCount, failureCount, sourceFile);
        }
        else
        {
            _logger.LogInformation("DNC API batch registration: All {Count} phone numbers registered successfully from source '{SourceFile}'", 
                phoneNumbers.Count, sourceFile);
        }

        return results;
    }

    /// <summary>
    /// Map PhoneNumber to DncApiRequest.
    /// </summary>
    private DncApiRequest MapToDncRequest(PhoneNumber phoneNumber, string sourceFile)
    {
        // Extract country code if available (assuming first digits might be country code)
        // This is a simple implementation - adjust based on your phone number format
        string? countryCode = null;

        // Try to extract country code from normalized number (e.g., if starts with country code)
        // This is a placeholder - adjust based on your actual phone number format
        if (phoneNumber.NormalizedNumber.Length > 10)
        {
            // Simple heuristic: if longer than 10 digits, might have country code
            countryCode = phoneNumber.NormalizedNumber.Substring(0, Math.Min(3, phoneNumber.NormalizedNumber.Length - 10));
        }

        return new DncApiRequest
        {
            Id = phoneNumber.Id,
            Number = phoneNumber.Number, // Use original number format
            CountryCode = countryCode,
            DncSource = sourceFile,
            Brand = null, // Set if you have brand information
            IsDoNotCall = true,
            DoNotSMS = true,
            CreatedDate = phoneNumber.FirstSeenAt,
            ExpirationDate = null, // Set expiration if needed
            ExtendedReference = phoneNumber.NormalizedNumber,
            SystemMeta = new SystemMeta { Status = true },
            ExternalReference = new ExternalReference
            {
                SourceSystem = "BlobToCosmosFunction",
                ReferenceId = phoneNumber.NormalizedNumber
            }
        };
    }
}
