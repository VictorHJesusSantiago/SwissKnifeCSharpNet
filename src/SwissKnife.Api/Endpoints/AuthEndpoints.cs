using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Auth;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Security;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Api.Endpoints;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName, Guid? TenantId);
public sealed record LoginRequest(string Email, string Password, string? MfaCode);
public sealed record RefreshRequest(string RefreshToken);
public sealed record MfaVerifyRequest(Guid UserId, string Code);

public static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        // API-012: registro local. Endpoint anônimo — a criação de conta em si não exige
        // sessão prévia, mas a senha forte e unicidade de e-mail são validadas no serviço.
        auth.MapPost("/register", async (RegisterRequest request, AuthService authService, CancellationToken ct) =>
        {
            var user = await authService.RegisterAsync(request.TenantId, request.Email, request.Password, request.DisplayName, ct);
            return Results.Created($"/api/auth/users/{user.Id}", new { user.Id, user.Email, user.DisplayName });
        }).AllowAnonymous();

        auth.MapPost("/login", async (LoginRequest request, AuthService authService, HttpContext http, CancellationToken ct) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString();
            var result = await authService.LoginAsync(request.Email, request.Password, request.MfaCode, ip, ct);
            if (!result.Success)
                return result.MfaRequired
                    ? Results.Json(new { error = result.Reason, mfaRequired = true, userId = result.PendingUserId }, statusCode: StatusCodes.Status401Unauthorized)
                    : Results.Json(new { error = result.Reason }, statusCode: StatusCodes.Status401Unauthorized);

            return Results.Ok(new { accessToken = result.Tokens!.AccessToken, refreshToken = result.Tokens.RefreshToken, expiresAt = result.Tokens.AccessTokenExpiresAt });
        }).AllowAnonymous();

        auth.MapPost("/refresh", async (RefreshRequest request, AuthService authService, HttpContext http, CancellationToken ct) =>
        {
            var tokens = await authService.RefreshAsync(request.RefreshToken, http.Connection.RemoteIpAddress?.ToString(), ct);
            return tokens is null
                ? Results.Json(new { error = "Refresh token inválido, expirado ou revogado." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(new { accessToken = tokens.AccessToken, refreshToken = tokens.RefreshToken, expiresAt = tokens.AccessTokenExpiresAt });
        }).AllowAnonymous();

        auth.MapPost("/logout", async (ITenantContext tenant, AuthService authService, CancellationToken ct) =>
        {
            if (tenant.PrincipalId is null) return Results.NoContent();
            await authService.RevokeAllSessionsAsync(tenant.PrincipalId.Value, ct);
            return Results.NoContent();
        });

        // API-013: enrolamento e verificação de MFA (TOTP) para o usuário autenticado.
        auth.MapPost("/mfa/enroll", async (ITenantContext tenant, SwissKnifeDbContext db, TotpService totpService, CancellationToken ct) =>
        {
            if (tenant.PrincipalId is null) return Results.Json(new { error = "Requer sessão de usuário (JWT), não API key de serviço." }, statusCode: StatusCodes.Status400BadRequest);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == tenant.PrincipalId, ct);
            if (user is null) return Results.NotFound();

            var enrollment = totpService.GenerateEnrollment();
            user.MfaSecretProtected = enrollment.ProtectedSecret;
            user.MfaRecoveryCodesProtected = enrollment.ProtectedRecoveryCodes;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                secret = enrollment.SecretBase32,
                otpAuthUri = totpService.BuildOtpAuthUri(enrollment.SecretBase32, user.Email),
                recoveryCodes = enrollment.RecoveryCodes
            });
        });

        // Anônimo de propósito: o usuário ainda não tem sessão completa neste ponto do
        // enrolamento — a prova de identidade aqui é o código TOTP correto para o UserId.
        auth.MapPost("/mfa/verify", async (MfaVerifyRequest request, SwissKnifeDbContext db, TotpService totpService, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == request.UserId, ct);
            if (user?.MfaSecretProtected is null) return Results.NotFound();
            if (!totpService.VerifyCode(user.MfaSecretProtected, request.Code))
                return Results.Json(new { error = "Código inválido." }, statusCode: StatusCodes.Status400BadRequest);

            user.MfaEnabled = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).AllowAnonymous();
    }
}
