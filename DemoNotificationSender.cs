using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ByltEx.Api;

public interface IDemoNotificationSender
{
    Task SendAsync(DemoRequest request, Guid requestId, CancellationToken cancellationToken);
}

public sealed class DemoNotificationSender : IDemoNotificationSender
{
    private readonly HttpClient _httpClient;
    private readonly ResendOptions _resend;
    private readonly DemoNotificationOptions _notification;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DemoNotificationSender> _logger;

    public DemoNotificationSender(
        HttpClient httpClient,
        IOptions<ResendOptions> resend,
        IOptions<DemoNotificationOptions> notification,
        IHostEnvironment environment,
        ILogger<DemoNotificationSender> logger)
    {
        _httpClient = httpClient;
        _resend = resend.Value;
        _notification = notification.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task SendAsync(
        DemoRequest request,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_resend.ApiKey))
        {
            if (_environment.IsDevelopment())
            {
                _logger.LogWarning(
                    "Resend API key not configured; skipping demo notification email for {DemoRequestId}",
                    requestId);
                return;
            }

            throw new InvalidOperationException("Resend API key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_resend.From))
        {
            throw new InvalidOperationException("Resend sender address (From) is not configured.");
        }

        var recipients = _notification.Recipients
            .Where(static e => !string.IsNullOrWhiteSpace(e))
            .Select(static e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (recipients.Length == 0)
        {
            throw new InvalidOperationException("No demo notification recipients configured.");
        }

        var name = request.Name.Trim();
        var email = request.Email.Trim();
        var company = request.Company.Trim();
        var role = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role.Trim();
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();

        var payload = new ResendSendEmailRequest
        {
            From = _resend.From.Trim(),
            To = recipients,
            ReplyTo = email,
            Subject = $"Demo request — {company}",
            Html = BuildHtml(requestId, name, email, company, role, phone, message),
            Text = BuildText(requestId, name, email, company, role, phone, message),
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(payload, options: ResendJson.Options),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _resend.ApiKey);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError(
            "Resend returned {StatusCode} for demo request {DemoRequestId}: {Body}",
            (int)response.StatusCode,
            requestId,
            body);
        throw new InvalidOperationException("Failed to send demo notification email.");
    }

    private static string BuildText(
        Guid requestId,
        string name,
        string email,
        string company,
        string? role,
        string? phone,
        string? message)
    {
        var sb = new StringBuilder();
        sb.AppendLine("New demo request");
        sb.AppendLine();
        sb.AppendLine($"Request ID: {requestId}");
        sb.AppendLine($"Name: {name}");
        sb.AppendLine($"Email: {email}");
        sb.AppendLine($"Company: {company}");
        AppendLineIfPresent(sb, "Role", role);
        AppendLineIfPresent(sb, "Phone", phone);
        if (!string.IsNullOrEmpty(message))
        {
            sb.AppendLine();
            sb.AppendLine("Message:");
            sb.AppendLine(message);
        }

        return sb.ToString();
    }

    private static string BuildHtml(
        Guid requestId,
        string name,
        string email,
        string company,
        string? role,
        string? phone,
        string? message)
    {
        var sb = new StringBuilder();
        sb.Append("<h2>New demo request</h2><table cellpadding=\"4\">");
        AppendRow(sb, "Request ID", requestId.ToString());
        AppendRow(sb, "Name", name);
        AppendRow(sb, "Email", email);
        AppendRow(sb, "Company", company);
        AppendRowIfPresent(sb, "Role", role);
        AppendRowIfPresent(sb, "Phone", phone);
        sb.Append("</table>");

        if (!string.IsNullOrEmpty(message))
        {
            sb.Append("<h3>Message</h3><p>");
            sb.Append(System.Net.WebUtility.HtmlEncode(message).ReplaceLineEndings("<br>"));
            sb.Append("</p>");
        }

        return sb.ToString();
    }

    private static void AppendLineIfPresent(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            sb.AppendLine($"{label}: {value}");
        }
    }

    private static void AppendRow(StringBuilder sb, string label, string value) =>
        sb.Append("<tr><th align=\"left\">")
            .Append(System.Net.WebUtility.HtmlEncode(label))
            .Append("</th><td>")
            .Append(System.Net.WebUtility.HtmlEncode(value))
            .Append("</td></tr>");

    private static void AppendRowIfPresent(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            AppendRow(sb, label, value);
        }
    }

    private sealed class ResendSendEmailRequest
    {
        [JsonPropertyName("from")]
        public required string From { get; init; }

        [JsonPropertyName("to")]
        public required string[] To { get; init; }

        [JsonPropertyName("reply_to")]
        public string? ReplyTo { get; init; }

        [JsonPropertyName("subject")]
        public required string Subject { get; init; }

        [JsonPropertyName("html")]
        public required string Html { get; init; }

        [JsonPropertyName("text")]
        public required string Text { get; init; }
    }

    private static class ResendJson
    {
        public static readonly System.Text.Json.JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
