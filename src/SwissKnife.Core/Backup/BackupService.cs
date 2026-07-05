using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Backup;

public sealed record BackupManifest(string SchemaVersion, DateTimeOffset CreatedAt, IReadOnlyDictionary<string, string> FileHashes);

/// <summary>
/// FND-035: empacota o arquivo de banco (SQLite) + diretório de anexos em um .zip com
/// manifesto validável. Para Postgres/SqlServer, a cópia do arquivo físico não se aplica;
/// nesses casos o backup usa o comando nativo do provider (não implementado nesta etapa
/// por exigir infraestrutura externa de dump — decisão documentada, válida para SQLite hoje).
/// </summary>
public sealed class SqliteBackupService(SwissKnifeDbContext db, string attachmentsRootDirectory)
{
    public async Task<string> CreateBackupAsync(string destinationZipPath, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        var dbPath = new SqliteConnectionStringBuilderCompat(connection.ConnectionString).DataSource;

        await db.Database.CloseConnectionAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationZipPath)!);
        if (File.Exists(destinationZipPath)) File.Delete(destinationZipPath);

        using (var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create))
        {
            if (File.Exists(dbPath))
                archive.CreateEntryFromFile(dbPath, "database.db");

            if (Directory.Exists(attachmentsRootDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(attachmentsRootDirectory, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.Combine("attachments", Path.GetRelativePath(attachmentsRootDirectory, file));
                    archive.CreateEntryFromFile(file, relative.Replace('\\', '/'));
                }
            }

            var manifest = new BackupManifest("1", DateTimeOffset.UtcNow, new Dictionary<string, string>());
            var manifestEntry = archive.CreateEntry("manifest.json");
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest, cancellationToken: cancellationToken);
        }

        return destinationZipPath;
    }

    public static async Task<BackupManifest> ValidateAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Backup sem manifest.json.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Manifesto de backup inválido.");
    }

    public static void Restore(string zipPath, string dbDestinationPath, string attachmentsDestinationDirectory)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var dbEntry = archive.GetEntry("database.db");
        if (dbEntry is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbDestinationPath)!);
            dbEntry.ExtractToFile(dbDestinationPath, overwrite: true);
        }
        Directory.CreateDirectory(attachmentsDestinationDirectory);
        foreach (var entry in archive.Entries.Where(x => x.FullName.StartsWith("attachments/") && !string.IsNullOrEmpty(x.Name)))
        {
            var relative = entry.FullName["attachments/".Length..];
            var destination = Path.Combine(attachmentsDestinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }
}

/// <summary>Extrai o caminho de arquivo de uma connection string SQLite sem depender do tipo interno do provider.</summary>
internal sealed class SqliteConnectionStringBuilderCompat
{
    public string DataSource { get; }

    public SqliteConnectionStringBuilderCompat(string connectionString)
    {
        DataSource = connectionString.Split(';')
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1].Trim())
            .FirstOrDefault() ?? "";
    }
}
