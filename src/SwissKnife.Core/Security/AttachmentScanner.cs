using SwissKnife.Core.Entities;

namespace SwissKnife.Core.Security;

/// <summary>FND-016: hook de antivírus para anexos. Sem integração externa disponível hoje,
/// a implementação padrão apenas marca como "Skipped" — decisão consciente, documentada,
/// substituível por um scanner real (ClamAV, Defender for Storage, etc.) implementando a
/// mesma interface.</summary>
public interface IAttachmentScanner
{
    Task<AttachmentScanStatus> ScanAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed class NoopAttachmentScanner : IAttachmentScanner
{
    public Task<AttachmentScanStatus> ScanAsync(string filePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(AttachmentScanStatus.Skipped);
}
