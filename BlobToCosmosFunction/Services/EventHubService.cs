using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using BlobToCosmosFunction.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BlobToCosmosFunction.Services;

public interface IEventHubService
{
    Task SendPhoneNumberAsync(PhoneNumber phoneNumber, string sourceFile, bool wasInserted, CancellationToken cancellationToken = default);
}

public class EventHubService : IEventHubService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EventHubService> _logger;
    private EventHubProducerClient? _producerClient;

    public EventHubService(
        IConfiguration configuration,
        ILogger<EventHubService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private EventHubProducerClient GetProducerClient()
    {
        if (_producerClient != null)
        {
            return _producerClient;
        }

        var connectionString = _configuration["EventHubConnectionString"];
        var eventHubName = _configuration["EventHubName"] ?? "phone-numbers";

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Event Hub connection string not configured. Set 'EventHubConnectionString' in configuration.");
        }

        // Create Event Hub Producer Client
        // Note: UseDevelopmentEmulator=true in connection string is handled automatically by Azure SDK
        _producerClient = new EventHubProducerClient(connectionString, eventHubName);
        
        var isEmulator = connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase) ||
                         connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase);
        
        _logger.LogInformation("Event Hub Producer Client initialized for Event Hub: {EventHubName} (Emulator: {IsEmulator})", 
            eventHubName, isEmulator);
        
        return _producerClient;
    }

    public async Task SendPhoneNumberAsync(PhoneNumber phoneNumber, string sourceFile, bool wasInserted, CancellationToken cancellationToken = default)
    {
        if (phoneNumber == null)
        {
            _logger.LogWarning("Cannot send null phone number to Event Hub");
            return;
        }

        try
        {
            var connectionString = _configuration["EventHubConnectionString"];
            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogDebug("Event Hub connection string not configured. Skipping Event Hub processing for phone number '{NormalizedNumber}'", 
                    phoneNumber.NormalizedNumber ?? "unknown");
                return;
            }

            // Check if this is an emulator connection and validate it's accessible
            var isEmulator = connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase) ||
                             connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase);
            
            if (isEmulator)
            {
                // Extract port from connection string for diagnostics
                var portMatch = System.Text.RegularExpressions.Regex.Match(connectionString, @":(\d+)");
                var port = portMatch.Success ? portMatch.Groups[1].Value : "unknown";
                _logger.LogDebug("Attempting to connect to Event Hub emulator on port {Port}", port);
            }

            var client = GetProducerClient();

            // Create event data with phone number information
            var eventData = new EventData(JsonSerializer.Serialize(new
            {
                PhoneNumber = phoneNumber.Number,
                NormalizedNumber = phoneNumber.NormalizedNumber,
                SourceFile = sourceFile,
                WasInserted = wasInserted,
                FirstSeenAt = phoneNumber.FirstSeenAt,
                LastSeenAt = phoneNumber.LastSeenAt,
                OccurrenceCount = phoneNumber.OccurrenceCount,
                SourceFiles = phoneNumber.SourceFiles,
                Timestamp = DateTime.UtcNow,
                EventType = wasInserted ? "PhoneNumberInserted" : "PhoneNumberExists"
            }, new JsonSerializerOptions { WriteIndented = false }));

            // Add metadata to event properties
            eventData.Properties.Add("NormalizedNumber", phoneNumber.NormalizedNumber ?? string.Empty);
            eventData.Properties.Add("SourceFile", sourceFile);
            eventData.Properties.Add("WasInserted", wasInserted.ToString());
            eventData.Properties.Add("EventType", wasInserted ? "PhoneNumberInserted" : "PhoneNumberExists");

            // Send event to Event Hub
            using var eventBatch = await client.CreateBatchAsync(cancellationToken);
            
            if (!eventBatch.TryAdd(eventData))
            {
                _logger.LogWarning("Event data too large for Event Hub batch. Phone number: '{NormalizedNumber}'", 
                    phoneNumber.NormalizedNumber ?? "unknown");
                return;
            }

            await client.SendAsync(eventBatch, cancellationToken);
            
            _logger.LogInformation("Successfully sent phone number '{NormalizedNumber}' to Event Hub (Inserted: {WasInserted})", 
                phoneNumber.NormalizedNumber ?? "unknown", wasInserted);
        }
        catch (System.Net.Sockets.SocketException socketEx) when (socketEx.ErrorCode == 10061)
        {
            // Connection refused - Event Hub emulator is not running
            var connectionString = _configuration["EventHubConnectionString"] ?? "";
            var portMatch = System.Text.RegularExpressions.Regex.Match(connectionString, @":(\d+)");
            var port = portMatch.Success ? portMatch.Groups[1].Value : "unknown";
            
            _logger.LogWarning(
                "Event Hub emulator is not running or not accessible on port {Port}. " +
                "Please ensure the AppHost is running to start the Event Hub emulator. " +
                "Phone number '{NormalizedNumber}' processing will continue without Event Hub publishing.",
                port, phoneNumber.NormalizedNumber ?? "unknown");
        }
        catch (Exception ex)
        {
            // Log Event Hub errors but don't fail the operation
            var connectionString = _configuration["EventHubConnectionString"] ?? "";
            var isEmulator = connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase);
            
            if (isEmulator)
            {
                _logger.LogWarning(ex, 
                    "Error connecting to Event Hub emulator. Ensure the AppHost is running to start the Event Hub emulator. " +
                    "Phone number '{NormalizedNumber}' processing will continue.",
                    phoneNumber.NormalizedNumber ?? "unknown");
            }
            else
            {
                _logger.LogWarning(ex, "Error sending phone number '{NormalizedNumber}' to Event Hub. Operation will continue.", 
                    phoneNumber.NormalizedNumber ?? "unknown");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_producerClient != null)
        {
            await _producerClient.DisposeAsync();
            _producerClient = null;
        }
    }
}
