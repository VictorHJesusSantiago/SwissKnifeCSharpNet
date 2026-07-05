using SwissKnife.Core.Tenancy;

namespace SwissKnife.Api;

/// <summary>Restringe um grupo de rotas a chamadas com escopo platform:admin (ou "*").</summary>
public sealed class PlatformAdminFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        if (!tenant.IsPlatformAdmin)
            return Results.Json(new { error = "Requer escopo platform:admin." }, statusCode: StatusCodes.Status403Forbidden);
        return await next(context);
    }
}
