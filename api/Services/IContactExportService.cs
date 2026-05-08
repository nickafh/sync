namespace AFHSync.Api.Services;

/// <summary>
/// Builds an Excel workbook (.xlsx bytes) listing the live (non-stale) source
/// contacts currently being synced, with one sheet per tunnel.
/// </summary>
public interface IContactExportService
{
    Task<byte[]> BuildContactsWorkbookAsync(CancellationToken ct);
}
