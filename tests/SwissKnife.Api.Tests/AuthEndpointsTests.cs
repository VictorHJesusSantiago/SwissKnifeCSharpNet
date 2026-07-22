using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SwissKnife.Api.Tests;

public sealed class AuthEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public AuthEndpointsTests(ApiTestFactory factory) => _factory = factory;

    private HttpClient BootstrapClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
        return client;
    }

    [Fact]
    public async Task Register_then_login_issues_jwt_that_authenticates_subsequent_requests()
    {
        using var anon = _factory.CreateClient();
        var email = $"user-{Guid.NewGuid():N}@example.com";

        var register = await anon.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = "uma-senha-bem-forte-123", DisplayName = "Fulano" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await anon.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = "uma-senha-bem-forte-123" });
        var loginBody = await login.Content.ReadAsStringAsync();
        Assert.True(login.StatusCode == HttpStatusCode.OK, loginBody);
        var loginJson = JsonDocument.Parse(loginBody).RootElement;
        var accessToken = loginJson.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));

        using var authedClient = _factory.CreateClient();
        authedClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        var whoAmI = await authedClient.GetAsync("/api/resources?module=snippets");
        var whoAmIBody = await whoAmI.Content.ReadAsStringAsync();
        Assert.True(whoAmI.StatusCode == HttpStatusCode.OK, whoAmIBody);
    }

    [Fact]
    public async Task Login_with_wrong_password_is_rejected()
    {
        using var anon = _factory.CreateClient();
        var email = $"user-{Guid.NewGuid():N}@example.com";
        await anon.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = "uma-senha-bem-forte-123" });

        var login = await anon.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = "senha-errada-mas-longa" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Refresh_token_issues_a_new_access_token_and_old_refresh_token_stops_working()
    {
        using var anon = _factory.CreateClient();
        var email = $"user-{Guid.NewGuid():N}@example.com";
        await anon.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = "uma-senha-bem-forte-123" });
        var login = await anon.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = "uma-senha-bem-forte-123" });
        var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        var refreshToken = loginJson.GetProperty("refreshToken").GetString();

        var refresh = await anon.PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = refreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        var secondRefresh = await anon.PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, secondRefresh.StatusCode);
    }

    [Fact]
    public async Task Mfa_enrollment_then_login_requires_totp_code()
    {
        using var anon = _factory.CreateClient();
        var email = $"user-{Guid.NewGuid():N}@example.com";
        await anon.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = "uma-senha-bem-forte-123" });
        var login = await anon.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = "uma-senha-bem-forte-123" });
        var accessToken = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();

        using var authedClient = _factory.CreateClient();
        authedClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        var enroll = await authedClient.PostAsync("/api/auth/mfa/enroll", null);
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var enrollJson = JsonDocument.Parse(await enroll.Content.ReadAsStringAsync()).RootElement;
        var secret = enrollJson.GetProperty("secret").GetString()!;

        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();
        // precisamos do userId; extraímos do JWT decodificando o claim "sub"
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(accessToken);
        var userId = token.Claims.First(c => c.Type == "sub").Value;

        var verify = await anon.PostAsJsonAsync("/api/auth/mfa/verify", new { UserId = Guid.Parse(userId), Code = code });
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);

        var loginWithoutMfa = await anon.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = "uma-senha-bem-forte-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, loginWithoutMfa.StatusCode);
        var body = JsonDocument.Parse(await loginWithoutMfa.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("mfaRequired").GetBoolean());

        var freshCode = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();
        var loginWithMfa = await anon.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = "uma-senha-bem-forte-123", MfaCode = freshCode });
        Assert.Equal(HttpStatusCode.OK, loginWithMfa.StatusCode);
    }
}
