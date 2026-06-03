namespace ByltEx.Api;

public sealed class DemoNotificationOptions
{
    public const string SectionName = "DemoNotification";

    public string[] Recipients { get; init; } =
    [
        "ash@byltex.com",
        "amir@byltex.com",
        "mobin@byltex.com",
    ];
}
