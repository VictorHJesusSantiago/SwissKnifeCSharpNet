using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Auditing;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Security;

namespace SwissKnife.Core.Auth;

public sealed record LoginResult(bool Success, string? Reason, IssuedTokenPair? Tokens, bool MfaRequired, Guid? PendingUserId);

public sealed class AuthService(SwissKnifeDbContext db, JwtTokenService jwt, TotpService totp, AuditLogger audit)
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    public async Task<UserEntity> RegisterAsync(Guid? tenantId, string email, string password, string? displayName, CancellationToken cancellationToken = default)
    {
        if (!PasswordHasher.IsStrong(password, out var reason)) throw new ArgumentException(reason);
        if (await db.Users.AnyAsync(x => x.Email == email, cancellationToken))
            throw new ArgumentException("Já existe um usuário com este e-mail.");

        var user = new UserEntity
        {
            TenantId = tenantId,
            Email = email,
            DisplayName = displayName,
            PasswordHash = PasswordHasher.Hash(password),
            PasswordChangedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(tenantId, user.Email, "user.registered", "User", user.Id.ToString(), cancellationToken: cancellationToken);
        return user;
    }

    public async Task<LoginResult> LoginAsync(string email, string password, string? mfaCode, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email, cancellationToken);
        if (user is null || user.PasswordHash is null)
        {
            await audit.LogAsync(null, email, "user.login.failed", "User", null, success: false, ipAddress: ipAddress, cancellationToken: cancellationToken);
            return new LoginResult(false, "Credenciais inválidas.", null, false, null);
        }

        if (user.LockedUntil is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow)
            return new LoginResult(false, "Conta temporariamente bloqueada por tentativas inválidas.", null, false, null);

        if (!user.IsActive || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= MaxFailedAttempts)
                user.LockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
            await db.SaveChangesAsync(cancellationToken);
            await audit.LogAsync(user.TenantId, email, "user.login.failed", "User", user.Id.ToString(), success: false, ipAddress: ipAddress, cancellationToken: cancellationToken);
            return new LoginResult(false, "Credenciais inválidas.", null, false, null);
        }

        if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(mfaCode))
                return new LoginResult(false, "Código MFA obrigatório.", null, true, user.Id);
            if (!totp.VerifyCode(user.MfaSecretProtected!, mfaCode))
            {
                await audit.LogAsync(user.TenantId, email, "user.login.mfa_failed", "User", user.Id.ToString(), success: false, ipAddress: ipAddress, cancellationToken: cancellationToken);
                return new LoginResult(false, "Código MFA inválido.", null, true, user.Id);
            }
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        var tokens = await IssueTokensAsync(user, ipAddress, cancellationToken);
        await audit.LogAsync(user.TenantId, email, "user.login.succeeded", "User", user.Id.ToString(), ipAddress: ipAddress, cancellationToken: cancellationToken);
        return new LoginResult(true, null, tokens, false, null);
    }

    public async Task<IssuedTokenPair> IssueTokensAsync(UserEntity user, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var accessToken = jwt.IssueAccessToken(user, user.ScopeList());
        var refreshPlain = JwtTokenService.GenerateRefreshToken();
        db.Set<RefreshTokenEntity>().Add(new RefreshTokenEntity
        {
            UserId = user.Id,
            TokenHash = JwtTokenService.HashRefreshToken(refreshPlain),
            ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshTokenLifetime),
            CreatedByIp = ipAddress
        });
        await db.SaveChangesAsync(cancellationToken);
        return new IssuedTokenPair(accessToken, refreshPlain, DateTimeOffset.UtcNow.Add(jwt.AccessTokenLifetime));
    }

    public async Task<IssuedTokenPair?> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var hash = JwtTokenService.HashRefreshToken(refreshToken);
        var entity = await db.Set<RefreshTokenEntity>().FirstOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (entity is null || !entity.IsActive(DateTimeOffset.UtcNow)) return null;

        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == entity.UserId, cancellationToken);
        if (user is null || !user.IsActive) return null;

        entity.RevokedAt = DateTimeOffset.UtcNow;
        var newTokens = await IssueTokensAsync(user, ipAddress, cancellationToken);
        return newTokens;
    }

    public async Task<bool> RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var tokens = await db.Set<RefreshTokenEntity>().Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in tokens) token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return tokens.Count > 0;
    }
}
