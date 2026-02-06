

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

//// Azure Cosmos DB emulator (container). Reference name "CosmosDB" is used by the Function for connection.
//var cosmos = builder.AddAzureCosmosDB("CosmosDB")
//    .RunAsEmulator();

// Azure Storage emulator (Azurite) for Blob trigger host storage and blob container
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator();

var blobs = storage.AddBlobs("blobs");

// Azure Event Hubs emulator
var eventHub = builder.AddAzureEventHubs("eventhubs-outbound")
    .RunAsEmulator(emulator => emulator
        .WithLifetime(ContainerLifetime.Persistent));

// Create Event Hub namespace and hub
var eventHubNamespace = eventHub.AddHub("phone-numbers");

// Azure Function with references to Storage and Event Hub (path resolved from AppHost project, not from bin output)
var appHostDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var functionProjectPath = Path.Combine(appHostDir, "BlobToCosmosFunction", "BlobToCosmosFunction.csproj");
var functions = builder.AddAzureFunctionsProject("blobfunction", functionProjectPath)
    .WithHostStorage(storage)
    .WithReference(blobs)
    .WithReference(eventHubNamespace);

builder.Build().Run();
