using System.Text.Json.Serialization;

namespace BlobToCosmosFunction.Models;

public class DncApiRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("systemMeta")]
    public SystemMeta? SystemMeta { get; set; }

    [JsonPropertyName("externalReference")]
    public ExternalReference? ExternalReference { get; set; }

    [JsonPropertyName("dncSource")]
    public string? DncSource { get; set; }

    [JsonPropertyName("brand")]
    public string? Brand { get; set; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("number")]
    public string Number { get; set; } = string.Empty;

    [JsonPropertyName("isDoNotCall")]
    public bool IsDoNotCall { get; set; } = true;

    [JsonPropertyName("doNotSMS")]
    public bool DoNotSMS { get; set; } = true;

    [JsonPropertyName("expirationDate")]
    public DateTime? ExpirationDate { get; set; }

    [JsonPropertyName("createdDate")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("extendedReference")]
    public string? ExtendedReference { get; set; }
}

public class SystemMeta
{
    [JsonPropertyName("status")]
    public bool Status { get; set; } = true;
}

public class ExternalReference
{
    [JsonPropertyName("sourceSystem")]
    public string? SourceSystem { get; set; }

    [JsonPropertyName("referenceId")]
    public string? ReferenceId { get; set; }
}
