using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

public sealed class RunClaimService(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    ILogger<RunClaimService> logger) : IRunClaimService
{
    public const string BlockedSummary = "another run was already in progress";

    public async Task<RunClaimResult> ClaimAsync(int? runId, RunType runType, bool isDryRun, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        // Advisory lock key 1 = sync run start serialisation. Postgres-specific and
        // transaction-scoped, so skip it on non-relational providers (the in-memory
        // provider used by unit tests) — mirrors the IsInMemory checks elsewhere.
        IDbContextTransaction? tx = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        await using var _tx = tx;
        if (tx is not null)
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1)", ct);

        SyncRun? requested = null;
        if (runId.HasValue)
        {
            requested = await db.SyncRuns.FirstOrDefaultAsync(r => r.Id == runId.Value, ct);
            if (requested is null)
            {
                await CommitAsync(tx, ct);
                return new RunClaimResult(RunClaimOutcome.NotFound, null);
            }
            if (requested.Status != SyncStatus.Pending)
            {
                await CommitAsync(tx, ct);
                return new RunClaimResult(RunClaimOutcome.AlreadyFinalized, requested);
            }
        }

        var now = DateTime.UtcNow;
        var alreadyRunning = await db.SyncRuns.AnyAsync(r => r.Status == SyncStatus.Running, ct);
        if (alreadyRunning)
        {
            if (requested is not null)
            {
                // Fail the requested row now rather than leaving it Pending for the 10-minute
                // cleanup — the UI shows the outcome immediately.
                requested.Status = SyncStatus.Failed;
                requested.CompletedAt = now;
                requested.ErrorSummary = BlockedSummary;
                await db.SaveChangesAsync(ct);
            }
            await CommitAsync(tx, ct);
            logger.LogWarning("Run claim blocked — another run is already Running (requested RunId={RunId})",
                runId?.ToString() ?? "new");
            return new RunClaimResult(RunClaimOutcome.Blocked, requested);
        }

        SyncRun run;
        if (requested is not null)
        {
            requested.Status = SyncStatus.Running;
            requested.StartedAt = now;
            run = requested;
        }
        else
        {
            run = new SyncRun
            {
                RunType = runType,
                Status = SyncStatus.Running,
                IsDryRun = isDryRun,
                StartedAt = now,
                CreatedAt = now
            };
            db.SyncRuns.Add(run);
        }
        await db.SaveChangesAsync(ct);
        await CommitAsync(tx, ct);

        logger.LogInformation("Claimed RunId={RunId} (RunType={RunType}, IsDryRun={IsDryRun}, RequestedTunnelIds={Tunnels})",
            run.Id, run.RunType, run.IsDryRun, run.RequestedTunnelIds ?? "all");
        return new RunClaimResult(RunClaimOutcome.Claimed, run);
    }

    private static async Task CommitAsync(IDbContextTransaction? tx, CancellationToken ct)
    {
        if (tx is not null)
            await tx.CommitAsync(ct);
    }
}
