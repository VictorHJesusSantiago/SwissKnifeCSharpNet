using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SwissKnife.Api.Tests;

public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public string ApiKey { get; } = "test-bootstrap-key";
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"swissknife-api-tests-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SwissKnife:DataDirectory"] = _dataDirectory,
                ["SwissKnife:ApiKey"] = ApiKey,
                ["SwissKnife:Database:Provider"] = "Sqlite",
                ["SwissKnife:Database:ConnectionString"] = $"Data Source={Path.Combine(_dataDirectory, "test.db")}"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }
}
