using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace AFHSync.Worker.Services;

/// <summary>
/// Fetches user Notes from Exchange Online via PowerShell Get-User.
/// The Entra admin "Notes" field (AD `info` attribute) is not available via
/// Microsoft Graph API — it requires Exchange Online PowerShell.
/// Follows the DDGResolver pattern for runspace management and auth.
/// </summary>
public interface IExchangeNotesResolver
{
    Task<Dictionary<string, string>> FetchNotesAsync(CancellationToken ct);
}

public class ExchangeNotesResolver : IExchangeNotesResolver, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<ExchangeNotesResolver> _logger;
    private Runspace? _runspace;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ExchangeNotesResolver(IConfiguration config, ILogger<ExchangeNotesResolver> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> FetchNotesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await _lock.WaitAsync(ct);
        try
        {
            var ps = GetOrCreatePowerShell();

            // Get-User returns Exchange recipient objects with the Notes property.
            // Select only what we need to minimize data transfer.
            ps.AddCommand("Get-User");
            ps.AddParameter("ResultSize", "Unlimited");
            ps.AddCommand("Select-Object");
            ps.AddParameter("Property", new[] { "UserPrincipalName", "Notes" });

            _logger.LogInformation("Fetching user Notes from Exchange Online via Get-User");
            var results = await Task.Run(() => ps.Invoke(), ct);

            if (ps.HadErrors)
            {
                var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                _logger.LogWarning("Get-User had errors (continuing with partial results): {Errors}", errors);
            }

            foreach (var obj in results)
            {
                var upn = obj.Properties["UserPrincipalName"]?.Value?.ToString();
                var notes = obj.Properties["Notes"]?.Value?.ToString();

                if (!string.IsNullOrEmpty(upn) && !string.IsNullOrWhiteSpace(notes))
                {
                    result[upn] = notes.Trim();
                }
            }

            _logger.LogInformation("Fetched Notes for {Count} users from Exchange Online", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch user Notes from Exchange Online, continuing without notes");
        }
        finally
        {
            _lock.Release();
        }

        return result;
    }

    private PowerShell GetOrCreatePowerShell()
    {
        if (_runspace == null || _runspace.RunspaceStateInfo.State != RunspaceState.Opened)
        {
            _logger.LogInformation("Creating Exchange Online PowerShell runspace for Notes resolver");

            var iss = InitialSessionState.CreateDefault();
            iss.ImportPSModule(["ExchangeOnlineManagement"]);
            if (OperatingSystem.IsWindows())
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.RemoteSigned;

            _runspace = RunspaceFactory.CreateRunspace(iss);
            _runspace.Open();

            using var connectPs = PowerShell.Create(_runspace);
            var connectCmd = connectPs.AddCommand("Connect-ExchangeOnline");

            var certPath = _config["Exchange:CertificatePath"];
            var certThumbprint = _config["Exchange:CertificateThumbprint"];

            if (!string.IsNullOrEmpty(certPath))
                connectCmd.AddParameter("CertificateFilePath", certPath);
            else if (!string.IsNullOrEmpty(certThumbprint))
                connectCmd.AddParameter("CertificateThumbprint", certThumbprint);
            else
                throw new InvalidOperationException(
                    "Exchange:CertificatePath or Exchange:CertificateThumbprint must be configured");

            connectCmd.AddParameter("AppID", _config["Exchange:AppId"]);
            connectCmd.AddParameter("Organization", _config["Exchange:Organization"]);
            connectCmd.AddParameter("ShowBanner", false);

            connectPs.Invoke();

            if (connectPs.HadErrors)
            {
                var errors = string.Join("; ", connectPs.Streams.Error.Select(e => e.ToString()));
                throw new InvalidOperationException($"Exchange Online connection failed: {errors}");
            }

            _logger.LogInformation("Connected to Exchange Online for Notes resolver");
        }

        var ps = PowerShell.Create(_runspace);
        ps.Commands.Clear();
        return ps;
    }

    public void Dispose()
    {
        _runspace?.Dispose();
    }
}
