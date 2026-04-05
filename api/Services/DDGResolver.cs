namespace AFHSync.Api.Services;

using System.Management.Automation;
using System.Management.Automation.Runspaces;

/// <summary>
/// Resolves Dynamic Distribution Groups from Exchange Online via PowerShell runspace.
/// Per D-01: Uses System.Management.Automation to invoke Exchange Online PowerShell.
/// Per D-02: Connects using certificate-based app-only auth (Exchange.ManageAsApp role).
/// Per D-03: Called during tunnel setup only (not during sync runs).
/// </summary>
public class DDGResolver : IDDGResolver, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<DDGResolver> _logger;
    private Runspace? _runspace;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DDGResolver(IConfiguration config, ILogger<DDGResolver> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DdgInfo>> ListDdgsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var ps = GetOrCreatePowerShell();

            ps.AddCommand("Get-DynamicDistributionGroup");
            ps.AddParameter("ResultSize", "Unlimited");

            _logger.LogInformation("Listing all Dynamic Distribution Groups from Exchange Online");
            var results = await Task.Run(() => ps.Invoke(), ct);

            if (ps.HadErrors)
            {
                var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                _logger.LogError("Exchange DDG listing failed: {Errors}", errors);
                throw new InvalidOperationException($"Exchange DDG listing failed: {errors}");
            }

            var ddgs = results.Select(ExtractDdgInfo).ToList();
            _logger.LogInformation("Retrieved {Count} Dynamic Distribution Groups", ddgs.Count);
            return ddgs;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DdgInfo?> GetDdgAsync(string identity, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var ps = GetOrCreatePowerShell();

            ps.AddCommand("Get-DynamicDistributionGroup");
            ps.AddParameter("Identity", identity);

            _logger.LogInformation("Getting DDG details for: {Identity}", identity);
            var results = await Task.Run(() => ps.Invoke(), ct);

            if (ps.HadErrors)
            {
                var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));

                // Check if this is a "not found" error
                if (errors.Contains("couldn't be found", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("DDG not found: {Identity}", identity);
                    return null;
                }

                _logger.LogError("Exchange DDG lookup failed for {Identity}: {Errors}", identity, errors);
                throw new InvalidOperationException($"Exchange DDG lookup failed: {errors}");
            }

            var result = results.FirstOrDefault();
            if (result == null)
            {
                _logger.LogWarning("DDG not found: {Identity}", identity);
                return null;
            }

            return ExtractDdgInfo(result);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Creates or reuses a PowerShell runspace connected to Exchange Online.
    /// Uses certificate-based auth with Exchange.ManageAsApp application role.
    /// </summary>
    private PowerShell GetOrCreatePowerShell()
    {
        if (_runspace == null || _runspace.RunspaceStateInfo.State != RunspaceState.Opened)
        {
            _logger.LogInformation("Creating new Exchange Online PowerShell runspace");

            var iss = InitialSessionState.CreateDefault();
            iss.ImportPSModule(["ExchangeOnlineManagement"]);
            if (OperatingSystem.IsWindows())
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.RemoteSigned;

            _runspace = RunspaceFactory.CreateRunspace(iss);
            _runspace.Open();

            // Connect to Exchange Online with certificate-based app-only auth
            using var connectPs = PowerShell.Create(_runspace);
            var connectCmd = connectPs.AddCommand("Connect-ExchangeOnline");

            var certPath = _config["Exchange:CertificatePath"];
            var certThumbprint = _config["Exchange:CertificateThumbprint"];

            if (!string.IsNullOrEmpty(certPath))
            {
                connectCmd.AddParameter("CertificateFilePath", certPath);
            }
            else if (!string.IsNullOrEmpty(certThumbprint))
            {
                connectCmd.AddParameter("CertificateThumbprint", certThumbprint);
            }
            else
            {
                throw new InvalidOperationException(
                    "Exchange:CertificatePath or Exchange:CertificateThumbprint must be configured");
            }

            connectCmd.AddParameter("AppID", _config["Exchange:AppId"]);
            connectCmd.AddParameter("Organization", _config["Exchange:Organization"]);
            connectCmd.AddParameter("ShowBanner", false);

            connectPs.Invoke();

            if (connectPs.HadErrors)
            {
                var errors = string.Join("; ", connectPs.Streams.Error.Select(e => e.ToString()));
                _logger.LogError("Exchange Online connection failed: {Errors}", errors);
                throw new InvalidOperationException($"Exchange Online connection failed: {errors}");
            }

            _logger.LogInformation("Connected to Exchange Online successfully");
        }

        var ps = PowerShell.Create(_runspace);
        ps.Commands.Clear();
        return ps;
    }

    /// <summary>
    /// Extracts DDG info from a PowerShell PSObject result.
    /// </summary>
    private static DdgInfo ExtractDdgInfo(PSObject result) => new(
        Id: result.Properties["Guid"]?.Value?.ToString() ?? string.Empty,
        DisplayName: result.Properties["DisplayName"]?.Value?.ToString() ?? string.Empty,
        PrimarySmtpAddress: result.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? string.Empty,
        RecipientFilter: result.Properties["RecipientFilter"]?.Value?.ToString() ?? string.Empty
    );

    public void Dispose()
    {
        _runspace?.Dispose();
        _lock.Dispose();
    }
}
