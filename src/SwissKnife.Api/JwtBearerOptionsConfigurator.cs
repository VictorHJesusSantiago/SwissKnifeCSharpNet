using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using SwissKnife.Core.Auth;

namespace SwissKnife.Api;

/// <summary>
/// Assim como o keyring do DataProtection, os parâmetros de validação do JWT só podem ser
/// montados com segurança depois do Build() (dependem de IConfiguration resolvido pela DI,
/// não de um snapshot lido antes do Build() — ver SwissKnifePaths para o motivo completo).
/// </summary>
public sealed class JwtBearerOptionsConfigurator(JwtTokenService jwtTokenService) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options) => Configure(options);

    public void Configure(JwtBearerOptions options)
    {
        options.TokenValidationParameters = jwtTokenService.ValidationParameters();
        options.MapInboundClaims = false;
    }
}
