namespace ByltEx.Api;

public sealed class SiteCorsOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; init; } =
    [
        "https://byltex.com",
        "http://localhost:4321",
    ];
}
