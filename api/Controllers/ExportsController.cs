using AFHSync.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/exports")]
public class ExportsController : ControllerBase
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IContactExportService _exportService;
    private readonly ILogger<ExportsController> _logger;

    public ExportsController(IContactExportService exportService, ILogger<ExportsController> logger)
    {
        _exportService = exportService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/exports/contacts.xlsx — Download an Excel workbook with one
    /// sheet per tunnel listing live (non-stale) contacts being synced.
    /// </summary>
    [HttpGet("contacts.xlsx")]
    public async Task<IActionResult> GetContactsXlsx(CancellationToken ct)
    {
        try
        {
            var bytes = await _exportService.BuildContactsWorkbookAsync(ct);
            var filename = $"afh-sync-contacts-{DateTime.UtcNow:yyyy-MM-dd}.xlsx";

            // File(bytes, contentType, fileDownloadName) sets Content-Disposition on
            // the content headers correctly so the client can read the filename.
            return File(bytes, XlsxContentType, filename);
        }
        catch (OperationCanceledException)
        {
            // Client cancelled — let ASP.NET handle the 499-equivalent
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build contacts export workbook");
            return StatusCode(500, new { message = "Failed to build contacts export." });
        }
    }
}
