using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Api;

/// <summary>Cabeçalhos defensivos aplicados inclusive a respostas de erro e rotas anônimas.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            return Task.CompletedTask;
        });
        await next(context);
    }
}

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

        // API-010/012/014: usuários autenticados via login local (JWT Bearer) são uma via
        // alternativa de identidade além da X-Api-Key de serviço-a-serviço. A validação do
        // token acontece aqui (via AuthenticateAsync do esquema JwtBearer já registrado),
        // não por [Authorize] em cada endpoint, para manter um único ponto de resolução de
        // tenant (FND-031).
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader) &&
            authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var result = await context.AuthenticateAsync(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
            if (!result.Succeeded || result.Principal is null)
            {
                await Unauthorized(context, "Token JWT inválido ou expirado.");
                return;
            }
            context.User = result.Principal;
            var userId = Guid.Parse(result.Principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!.Value);
            var tenantClaim = result.Principal.FindFirst("tenant")?.Value;
            var scopesClaim = result.Principal.FindFirst("scopes")?.Value ?? "";
            var scopes = scopesClaim.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tenantId = string.IsNullOrEmpty(tenantClaim) ? await EnsureBootstrapTenantAsync(db) : Guid.Parse(tenantClaim);
            tenantAccessor.Current.Resolve(tenantId, userId, scopes, result.Principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value);
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(LegacyBootstrapKeyHeader, out var supplied) || string.IsNullOrWhiteSpace(supplied))
        {
            await Unauthorized(context, "X-Api-Key ou Authorization Bearer ausente.");
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

/// <summary>Correlaciona todas as requisições com um X-Correlation-Id (recebido ou gerado), propagado nos logs e nas respostas de erro (API-004).</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var supplied) && !string.IsNullOrWhiteSpace(supplied)
            ? supplied.ToString()
            : Guid.NewGuid().ToString("N");

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        await next(context);
    }
}

/// <summary>API-004: erros padronizados em RFC 9457 Problem Details, sempre com correlation ID.</summary>
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
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Requisição inválida", exception.Message);
        }
        catch (JsonException exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "JSON inválido", exception.Message);
        }
        catch (Core.Repositories.ResourceValidationException exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "Payload inválido para o schema do módulo",
                "Um ou mais campos não atendem ao schema do módulo.", new Dictionary<string, object?> { ["errors"] = exception.Errors });
        }
        catch (Core.Repositories.ConcurrencyConflictException exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "Conflito de concorrência", exception.Message);
        }
        catch (Core.Repositories.DuplicateResourceNameException exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "Nome duplicado", exception.Message);
        }
        catch (Core.Repositories.InvalidStateTransitionException exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Transição de estado inválida", exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, "Não encontrado", exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha não tratada em {Path} (correlationId={CorrelationId})", context.Request.Path, context.Items[CorrelationIdMiddleware.HeaderName]);
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "Erro interno", "Ocorreu um erro inesperado.");
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail, Dictionary<string, object?>? extensions = null)
    {
        context.Response.StatusCode = statusCode;
        var problem = new Dictionary<string, object?>
        {
            ["type"] = $"https://httpstatuses.com/{statusCode}",
            ["title"] = title,
            ["status"] = statusCode,
            ["detail"] = detail,
            ["instance"] = context.Request.Path.ToString(),
            ["correlationId"] = context.Items[CorrelationIdMiddleware.HeaderName]
        };
        if (extensions is not null)
            foreach (var (key, value) in extensions) problem[key] = value;

        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}

/// <summary>API-031: modo de manutenção — quando ativo (config dinâmica "maintenance.enabled"), bloqueia rotas mutáveis não anônimas com 503.</summary>
public sealed class MaintenanceModeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, Core.Configuration.DynamicConfigService config)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        var isMutating = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
        if (!isMutating)
        {
            await next(context);
            return;
        }

        var maintenance = await config.GetAsync("maintenance.enabled", null);
        if (maintenance == "true")
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "A plataforma está em modo de manutenção. Tente novamente em instantes." });
            return;
        }

        await next(context);
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
