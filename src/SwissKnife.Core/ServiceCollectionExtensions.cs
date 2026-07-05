using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwissKnife.Core.Backup;
using SwissKnife.Core.Eventing;
using SwissKnife.Core.ImportExport;
using SwissKnife.Core.Idempotency;
using SwissKnife.Core.Jobs;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Repositories;
using SwissKnife.Core.Search;
using SwissKnife.Core.Security;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra toda a Fundação (FND-001..040): persistência, tenancy, segurança, jobs,
    /// outbox, import/export, busca e backup. Não recebe caminhos pré-computados — todos os
    /// diretórios são resolvidos lazily via <see cref="SwissKnifePaths"/> (ver o comentário
    /// naquela classe sobre por que isso importa para overrides de configuração em testes).
    /// </summary>
    public static IServiceCollection AddSwissKnifeCore(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var environment = sp.GetRequiredService<IHostEnvironment>();
            var configured = configuration["SwissKnife:DataDirectory"] ?? "data";
            return new SwissKnifePaths(Path.GetFullPath(configured, environment.ContentRootPath));
        });

        services.AddSingleton<TenantContextAccessor>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContextAccessor>().Current);
        services.AddScoped<DomainSaveChangesInterceptor>();

        services.AddDbContext<SwissKnifeDbContext>((sp, options) =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var paths = sp.GetRequiredService<SwissKnifePaths>();
            var provider = DatabaseProviderConfigurator.Parse(configuration["SwissKnife:Database:Provider"]);
            var connectionString = configuration["SwissKnife:Database:ConnectionString"]
                ?? $"Data Source={Path.Combine(paths.DataDirectory, "swissknife.db")}";
            DatabaseProviderConfigurator.Configure(options, provider, connectionString);
            options.AddInterceptors(sp.GetRequiredService<DomainSaveChangesInterceptor>());
        });

        // O caminho do keyring só pode ser conhecido em tempo de execução (via
        // SwissKnifePaths), então a localização do XmlRepository é configurada por um
        // IConfigureOptions resolvido pela DI, em vez de PersistKeysToFileSystem com um
        // DirectoryInfo fixo calculado antes do Build().
        services.AddDataProtection();
        services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>>(sp =>
            new ConfigureDataProtectionKeyPath(sp.GetRequiredService<SwissKnifePaths>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ResourceRepository>();
        services.AddScoped(sp => new ResourceExtrasRepository(
            sp.GetRequiredService<SwissKnifeDbContext>(),
            sp.GetRequiredService<TenantContextAccessor>(),
            sp.GetRequiredService<IAttachmentScanner>(),
            sp.GetRequiredService<SwissKnifePaths>().AttachmentsDirectory));
        services.AddScoped<ImportExportService>();
        services.AddScoped<SearchService>();
        services.AddScoped<IdempotencyStore>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<TenantService>();
        services.AddScoped<ISecretVault, DataProtectionSecretVault>();
        services.AddSingleton<PayloadProtector>();
        services.AddSingleton<IAttachmentScanner, NoopAttachmentScanner>();
        services.AddScoped(sp => new SqliteBackupService(sp.GetRequiredService<SwissKnifeDbContext>(), sp.GetRequiredService<SwissKnifePaths>().AttachmentsDirectory));

        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddSingleton<JobQueue>();
        services.AddScoped<IJobHandler, PurgeExpiredResourcesJobHandler>();
        services.AddHostedService<OutboxDispatcherHostedService>();
        services.AddHostedService<JobDispatcherHostedService>();
        services.AddSingleton(sp => (JobDispatcherHostedService)sp.GetServices<IHostedService>().First(x => x is JobDispatcherHostedService));
        services.AddHostedService<ScheduledJobRunnerHostedService>();
        services.AddHostedService<RetentionSweepHostedService>();

        return services;
    }
}

file sealed class ConfigureDataProtectionKeyPath(SwissKnifePaths paths, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    : Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>
{
    public void Configure(Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions options) =>
        options.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(new DirectoryInfo(paths.KeyRingDirectory), loggerFactory);
}
