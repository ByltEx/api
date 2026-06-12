namespace ByltEx.Api;

public record DemoRequest(
    string Name,
    string Email,
    string Company,
    string? Role,
    string? Phone,
    string? Message,
    string? ReplyTo);

public record DemoRequestResponse(Guid Id);
