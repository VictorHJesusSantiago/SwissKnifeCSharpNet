using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Api;

/// <summary>
/// FND-031 + evolução de API-015/016: resolve a chave enviada em X-Api-Key contra a tabela
/// ApiKeys (hash). Mantém compatibilidade com a chave legada de appsettings como uma
/// "chave de bootstrap" ligada ao tenant "default", com todos os escopos, até que o
/// operador crie chaves reais via /api/tenants.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration, IHostEnvironment environment)
{
    public const string LegacyBootstrapKeyHeader = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context, SwissKnifeDbContext db, ApiKeyService apiKeyService, TenantContextAccessor tenantAccessor)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(LegacyBootstrapKeyHeader, out var supplied) || string.IsNullOrWhiteSpace(supplied))
        {
            await Unauthorized(context, "X-Api-Key ausente.");
            return;
        }

        var suppliedKey = supplied.ToString();

        var legacyBootstrapKey = configuration["SwissKnife:ApiKey"];
        var bootstrapConfigured = !string.IsNullOrWhiteSpace(legacyBootstrapKey) && legacyBootstrapKey != "change-me-in-production";
        var effectiveBootstrapKey = bootstrapConfigured ? legacyBootstrapKey : (environment.IsDevelopment() ? "dev-key" : null);

        if (effectiveBootstrapKey is not null &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(suppliedKey),
                System.Text.Encoding.UTF8.GetBytes(effectiveBootstrapKey)))
        {
            var bootstrapTenant = await EnsureBootstrapTenantAsync(db);
            tenantAccessor.Current.Resolve(bootstrapTenant, Guid.Empty, ["*"], "bootstrap");
            await next(context);
            return;
        }

        var resolved = await apiKeyService.ResolveAsync(suppliedKey);
        if (resolved is null)
        {
            await Unauthorized(context, "X-Api-Key inválida, expirada ou revogada.");
            return;
        }

        tenantAccessor.Current.Resolve(resolved.TenantId, resolved.Id, resolved.ScopeList(), resolved.Name);
        await next(context);
    }

    private static async Task<Guid> EnsureBootstrapTenantAsync(SwissKnifeDbContext db)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(x => x.Slug == "default");
        if (tenant is not null) return tenant.Id;

        tenant = new Core.Entities.Tenant { Slug = "default", DisplayName = "Tenant padrão (bootstrap)" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task Unauthorized(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}

/// <summary>
/// FND-031: garante que o tenant efetivo de toda operação mutável vem do contexto de
/// autenticação, nunca do corpo da requisição. Este middleware roda depois do
/// ApiKeyMiddleware e só valida que o TenantContext foi de fato resolvido.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContextAccessor tenantAccessor)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        if (tenantAccessor.Current.TenantId == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Tenant não resolvido para esta requisição." });
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
        catch (Core.Repositories.ResourceValidationException exception)
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await context.Response.WriteAsJsonAsync(new { error = "Payload inválido para o schema do módulo.", details = exception.Errors });
        }
        catch (Core.Repositories.ConcurrencyConflictException exception)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        catch (Core.Repositories.DuplicateResourceNameException exception)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        catch (Core.Repositories.InvalidStateTransitionException exception)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
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

/// <summary>FND-039: honra o header Idempotency-Key em comandos mutáveis (POST/PUT/DELETE/PATCH).</summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, Core.Idempotency.IdempotencyStore store, TenantContextAccessor tenantAccessor)
    {
        var isMutating = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);

        if (!isMutating || !context.Request.Headers.TryGetValue("Idempotency-Key", out var keyHeader) || string.IsNullOrWhiteSpace(keyHeader))
        {
            await next(context);
            return;
        }

        var key = keyHeader.ToString();
        var endpoint = context.Request.Path.ToString();

        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
        var requestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        var tenantId = tenantAccessor.Current.TenantId;
        var cached = await store.TryGetAsync(key, tenantId, endpoint, requestHash);
        if (cached is not null)
        {
            context.Response.StatusCode = cached.StatusCode;
            context.Response.Headers["Idempotency-Replayed"] = "true";
            if (cached.BodyJson is not null)
                await context.Response.WriteAsync(cached.BodyJson);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        await next(context);

        buffer.Position = 0;
        var responseText = await new StreamReader(buffer).ReadToEndAsync();
        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody);
        context.Response.Body = originalBody;

        if (context.Response.StatusCode is >= 200 and < 500)
            await store.SaveAsync(key, tenantId, endpoint, requestHash, context.Response.StatusCode, responseText);
    }
}

public sealed record LogInput(
    string Level,
    string Source,
    string Message,
    string Tenant = "default",
    Dictionary<string, string>? Properties = null);
