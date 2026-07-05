namespace SwissKnife.Core;

/// <summary>
/// Diretórios de dados resolvidos em tempo de execução (via DI), nunca capturados como
/// string antes de "builder.Build()". Ler IConfiguration diretamente em Program.cs antes do
/// Build() captura um snapshot que ignora overrides aplicados depois (por exemplo, pelo
/// WebApplicationFactory em testes, que injeta configuração de teste apenas no momento do
/// Build() via um host builder adiado) — por isso esses caminhos só são calculados quando
/// este singleton é resolvido pela primeira vez, o que sempre acontece depois do Build().
/// </summary>
public sealed class SwissKnifePaths
{
    public string DataDirectory { get; }
    public string KeyRingDirectory { get; }
    public string AttachmentsDirectory { get; }
    public string BackupsDirectory { get; }

    public SwissKnifePaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        KeyRingDirectory = Path.Combine(dataDirectory, "keys");
        AttachmentsDirectory = Path.Combine(dataDirectory, "attachments");
        BackupsDirectory = Path.Combine(dataDirectory, "backups");

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(KeyRingDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
