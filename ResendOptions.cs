namespace ByltEx.Api;

public sealed class ResendOptions
{
    public const string SectionName = "Resend";

    public string? ApiKey { get; init; }

    /// <summary>Sender address for outbound mail (must be a verified Resend domain).</summary>
    public string? From { get; init; }
}
