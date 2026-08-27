namespace AFHSync.Api.Services;

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

/// <summary>
/// Resolves Dynamic Distribution Groups from Exchange Online via a PowerShell runspace.
/// Per D-01: System.Management.Automation invokes Exchange Online PowerShell.
/// Per D-02: certificate-based app-only auth (Exchange.ManageAsApp role).
///
/// Phase 3 (§3.6): registered as a Singleton in both api and worker — one Exchange session per
/// process instead of one connect per request. A failed Connect-ExchangeOnline disposes the
/// runspace so the next call reconnects from scratch; a command that fails with a session/auth
/// error tears the runspace down and retries exactly once; Dispose runs Disconnect-ExchangeOnline.
/// All Exchange calls are serialised by <see cref="_lock"/>.
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
            _logger.LogInformation("Listing all Dynamic Distribution Groups from Exchange Online");
            var (results, errors) = await InvokeWithSessionRetryAsync(ps =>
            {
                ps.AddCommand("Get-DynamicDistributionGroup");
                ps.AddParameter("ResultSize", "Unlimited");
            }, ct);

            if (errors is not null)
            {
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
            _logger.LogInformation("Getting DDG details for: {Identity}", identity);
            var (results, errors) = await InvokeWithSessionRetryAsync(ps =>
            {
                ps.AddCommand("Get-DynamicDistributionGroup");
                ps.AddParameter("Identity", identity);
            }, ct);

            if (errors is not null)
            {
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
    /// Phase 3 (§3.6): the failures Exchange reports when the remote session was torn down or the
    /// app token expired — the runspace is useless and must be rebuilt.
    /// </summary>
    public static bool IsSessionError(string? errors, Exception? exception = null)
    {
        if (exception is UnauthorizedAccessException)
            return true;

        var text = errors ?? exception?.Message;
        if (string.IsNullOrEmpty(text))
            return false;

        return text.Contains("session", StringComparison.OrdinalIgnoreCase)
            || text.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs one command on the shared runspace. If it fails with a session/auth error the runspace
    /// is disposed and the command retried exactly once on a fresh connection. Caller holds _lock.
    /// Returns the results and, when the pipeline reported errors, their joined text.
    /// </summary>
    private async Task<(Collection<PSObject> Results, string? Errors)> InvokeWithSessionRetryAsync(
        Action<PowerShell> build, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var ps = GetOrCreatePowerShell();
            build(ps);

            Collection<PSObject> results;
            string? errors = null;
            try
            {
                results = await Task.Run(() => ps.Invoke(), ct);
                if (ps.HadErrors)
                    errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt == 1 && IsSessionError(null, ex))
            {
                _logger.LogWarning(ex, "Exchange Online session error — resetting the runspace and retrying once");
                ResetRunspace();
                continue;
            }

            if (errors is not null && attempt == 1 && IsSessionError(errors))
            {
                _logger.LogWarning("Exchange Online session error ({Errors}) — resetting the runspace and retrying once", errors);
                ResetRunspace();
                continue;
            }

            return (results, errors);
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

            var runspace = RunspaceFactory.CreateRunspace(iss);
            try
            {
                runspace.Open();
                ConnectExchangeOnline(runspace);
            }
            catch
            {
                // Phase 3 (§3.6): never keep an opened-but-unconnected runspace — every later
                // command would run against it and fail. Dispose so the next call reconnects.
                runspace.Dispose();
                throw;
            }

            _runspace?.Dispose();
            _runspace = runspace;
            _logger.LogInformation("Connected to Exchange Online successfully");
        }

        var ps = PowerShell.Create(_runspace);
        ps.Commands.Clear();
        return ps;
    }

    private void ConnectExchangeOnline(Runspace runspace)
    {
        using var connectPs = PowerShell.Create(runspace);
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
    }

    private void ResetRunspace()
    {
        DisconnectQuietly();
        _runspace?.Dispose();
        _runspace = null;
    }

    /// <summary>Best-effort Disconnect-ExchangeOnline so the tenant-side session is released.</summary>
    private void DisconnectQuietly()
    {
        if (_runspace is null || _runspace.RunspaceStateInfo.State != RunspaceState.Opened)
            return;
        try
        {
            using var ps = PowerShell.Create(_runspace);
            ps.AddCommand("Disconnect-ExchangeOnline").AddParameter("Confirm", false);
            ps.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disconnect-ExchangeOnline failed (ignored)");
        }
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
        DisconnectQuietly();
        _runspace?.Dispose();
        _runspace = null;
        _lock.Dispose();
    }
}
