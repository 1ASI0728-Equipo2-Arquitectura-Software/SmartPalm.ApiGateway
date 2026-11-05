namespace SmartPalm.ApiGateway.Swagger;

public sealed class SwaggerServicesOptions
{
    public const string SectionName = "SwaggerServices";

    public string Identity { get; set; } = string.Empty;
    public string Crop { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public string Ingestion { get; set; } = string.Empty;
    public string Alert { get; set; } = string.Empty;
    public string Agronomy { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;

    public IReadOnlyList<SwaggerServiceDefinition> GetDefinitions() =>
    [
        new("Identity", "Identity", Identity),
        new("Crop", "Crop", Crop),
        new("Device", "Device", Device),
        new("Ingestion", "Ingestion", Ingestion),
        new("Alert", "Alert", Alert),
        new("Agronomy", "Agronomy", Agronomy),
        new("Field", "Field", Field)
    ];
}

public sealed record SwaggerServiceDefinition(
    string Key,
    string DisplayName,
    string BaseAddress);
