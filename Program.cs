using System.Globalization;
using System.Threading.RateLimiting;
using ByltEx.Api;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DemoRateLimitOptions>(
    builder.Configuration.GetSection(DemoRateLimitOptions.SectionName));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust proxy headers from the ingress/gateway (single-hop in cluster).
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
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

app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok());

app.MapPost("/api/demo", async (DemoRequest request, ILogger<Program> logger) =>
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

    await Task.CompletedTask;
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

record DemoRequest(
    string Name,
    string Email,
    string Company,
    string? Role,
    string? Phone,
    string? Message);

record DemoRequestResponse(Guid Id);
