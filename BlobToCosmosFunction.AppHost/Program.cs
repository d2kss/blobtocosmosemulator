// Aspire AppHost: runs Cosmos DB emulator + Azure Storage (Azurite) + BlobToCosmos Azure Function.
// See https://aspire.dev/integrations/cloud/azure/azure-cosmos-db/
// and https://aspire.dev/integrations/cloud/azure/azure-functions/

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Azure Cosmos DB emulator (container). Reference name "CosmosDB" is used by the Function for connection.
var cosmos = builder.AddAzureCosmosDB("CosmosDB")
    .RunAsEmulator();

// Azure Storage emulator (Azurite) for Blob trigger host storage and blob container
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator();

var blobs = storage.AddBlobs("blobs");

// Azure Function with references to Cosmos DB and Storage (path resolved from AppHost project, not from bin output)
var appHostDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var functionProjectPath = Path.Combine(appHostDir, "BlobToCosmosFunction", "BlobToCosmosFunction.csproj");
var functions = builder.AddAzureFunctionsProject("blobfunction", functionProjectPath)
    .WithHostStorage(storage)
    .WithReference(cosmos)
    .WithReference(blobs);

builder.Build().Run();
