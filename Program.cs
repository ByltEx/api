using System.Globalization;
using System.Threading.RateLimiting;
using ByltEx.Api;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DemoRateLimitOptions>(
    builder.Configuration.GetSection(DemoRateLimitOptions.SectionName));
builder.Services.Configure<ResendOptions>(
    builder.Configuration.GetSection(ResendOptions.SectionName));
builder.Services.Configure<DemoNotificationOptions>(
    builder.Configuration.GetSection(DemoNotificationOptions.SectionName));
builder.Services.Configure<SiteCorsOptions>(
    builder.Configuration.GetSection(SiteCorsOptions.SectionName));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust proxy headers from the ingress/gateway (single-hop in cluster).
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var corsOrigins = builder.Configuration
    .GetSection(SiteCorsOptions.SectionName)
    .Get<SiteCorsOptions>()?.AllowedOrigins ?? new SiteCorsOptions().AllowedOrigins;

builder.Services.AddCors(options =>
{
    options.AddPolicy("website", policy =>
    {
        policy.WithOrigins(corsOrigins)
            .WithMethods("POST", "OPTIONS")
            .WithHeaders("Content-Type");
    });
});

builder.Services.AddHttpClient<IDemoNotificationSender, DemoNotificationSender>(client =>
{
    client.BaseAddress = new Uri("https://api.resend.com/");
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
});

builder.Services.AddRateLimiter(options =>
{
    var limits = builder.Configuration
        .GetSection(DemoRateLimitOptions.SectionName)
        .Get<DemoRateLimitOptions>() ?? new DemoRateLimitOptions();

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many demo requests. Please try again later." },
            cancellationToken: cancellationToken);
    };

    options.AddPolicy("demo", httpContext =>
    {
        var partitionKey = GetClientPartitionKey(httpContext);
        return RateLimitPartition.Get(
            partitionKey,
            _ => new CompositeFixedWindowRateLimiter([
                CreateWindowOptions(limits.PerMinute, TimeSpan.FromMinutes(1)),
                CreateWindowOptions(limits.PerHour, TimeSpan.FromHours(1)),
                CreateWindowOptions(limits.PerDay, TimeSpan.FromDays(1)),
            ]));
    });
});

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors("website");
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok());

app.MapPost("/api/demo", async (
    DemoRequest request,
    IDemoNotificationSender notificationSender,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (string.IsNullOrWhiteSpace(request.Name))
    {
        errors["name"] = ["Name is required."];
    }

    if (string.IsNullOrWhiteSpace(request.Email))
    {
        errors["email"] = ["Email is required."];
    }
    else if (!request.Email.Contains('@', StringComparison.Ordinal))
    {
        errors["email"] = ["Email is not valid."];
    }

    if (string.IsNullOrWhiteSpace(request.Company))
    {
        errors["company"] = ["Company is required."];
    }

    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var id = Guid.NewGuid();
    logger.LogInformation(
        "Demo request {DemoRequestId}: {Name} <{Email}> at {Company}",
        id,
        request.Name.Trim(),
        request.Email.Trim(),
        request.Company.Trim());

    try
    {
        await notificationSender.SendAsync(request, id, cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to send demo notification for {DemoRequestId}", id);
        return Results.Problem(
            title: "Unable to submit demo request",
            detail: "Something went wrong on our end. Please try again later.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Created($"/api/demo/{id}", new DemoRequestResponse(id));
})
.RequireRateLimiting("demo")
.WithName("CreateDemoRequest");

app.Run();

static FixedWindowRateLimiterOptions CreateWindowOptions(int permitLimit, TimeSpan window) =>
    new()
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
    };

static string GetClientPartitionKey(HttpContext httpContext)
{
    var ip = httpContext.Connection.RemoteIpAddress;
    return ip is null ? "unknown" : ip.ToString();
}

record DemoRateLimitOptions
{
    public const string SectionName = "RateLimiting:Demo";

    public int PerMinute { get; init; } = 1;
    public int PerHour { get; init; } = 3;
    public int PerDay { get; init; } = 5;
}
