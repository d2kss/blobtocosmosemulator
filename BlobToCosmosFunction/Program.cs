using System.Net;
using BlobToCosmosFunction.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // Register IConfiguration explicitly
        services.AddSingleton<IConfiguration>(context.Configuration);

        // Register services with IConfiguration injection
        services.AddSingleton<IBlobStorageService, BlobStorageService>();
        services.AddSingleton<IFileParserService, FileParserService>();
        services.AddSingleton<IPhoneNumberService, PhoneNumberService>();
        services.AddSingleton<ICosmosDbService, CosmosDbService>();
        
        // Register Event Hub service
        services.AddSingleton<IEventHubService, EventHubService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

// Log startup information
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
var logger = loggerFactory.CreateLogger("Startup");
logger.LogInformation("========================================");
logger.LogInformation("Azure Function Starting...");
logger.LogInformation("  - Storage: Cosmos DB (emulator or Azure)");
logger.LogInformation("Blob Trigger: input-files/{{name}}, Polling: 1s");
logger.LogInformation("========================================");

host.Run();
