using System.Text.Json;

namespace SwissKnife.Api;

public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration, IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        var configured = configuration["SwissKnife:ApiKey"];
        if (string.IsNullOrWhiteSpace(configured) || configured == "change-me-in-production")
        {
            if (!environment.IsDevelopment())
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { error = "Configure SwissKnife:ApiKey antes de executar em produção." });
                return;
            }
            configured = "dev-key";
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var supplied) ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(supplied.ToString()),
                System.Text.Encoding.UTF8.GetBytes(configured)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "X-Api-Key ausente ou inválida." });
            return;
        }

        await next(context);
    }
}

public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ArgumentException exception)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        catch (JsonException exception)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha não tratada em {Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Erro interno." });
        }
    }
}

public sealed record LogInput(
    string Level,
    string Source,
    string Message,
    string Tenant = "default",
    Dictionary<string, string>? Properties = null);
