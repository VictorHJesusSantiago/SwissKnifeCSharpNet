using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SwissKnife.Core.Entities;

namespace SwissKnife.Core.Auth;

public sealed record IssuedTokenPair(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt);

/// <summary>
/// API-014: sessões via JWT de curta duração (access token) + refresh token opaco de longa
/// duração armazenado com hash no banco (RefreshTokenEntity), permitindo revogação/logout
/// global. A chave de assinatura HMAC vem de configuração (SwissKnife:Jwt:SigningKey);
/// sem KMS externo — decisão consciente, mesma classe de trade-off já documentada para
/// DataProtection na Fundação.
/// </summary>
public sealed class JwtTokenService(IConfiguration configuration)
{
    private readonly string _issuer = configuration["SwissKnife:Jwt:Issuer"] ?? "swissknife";
    private readonly string _audience = configuration["SwissKnife:Jwt:Audience"] ?? "swissknife-clients";
    private readonly TimeSpan _accessTokenLifetime = TimeSpan.FromMinutes(configuration.GetValue("SwissKnife:Jwt:AccessTokenMinutes", 15));

    private SymmetricSecurityKey SigningKey()
    {
        var configured = configuration["SwissKnife:Jwt:SigningKey"];
        var keyText = string.IsNullOrWhiteSpace(configured) ? "dev-only-insecure-signing-key-change-me-1234567890" : configured;
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyText));
    }

    public string IssueAccessToken(UserEntity user, IReadOnlyList<string> effectiveScopes)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("tenant", user.TenantId?.ToString() ?? ""),
            new("scopes", string.Join(',', effectiveScopes))
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(_accessTokenLifetime),
            signingCredentials: new SigningCredentials(SigningKey(), SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = _issuer,
        ValidAudience = _audience,
        IssuerSigningKey = SigningKey(),
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    public static string GenerateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public TimeSpan AccessTokenLifetime => _accessTokenLifetime;
}
