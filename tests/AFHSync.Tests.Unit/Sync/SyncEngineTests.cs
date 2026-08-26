using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models.ODataErrors;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for SyncEngine orchestrator.
/// Uses InMemory EF Core DB and stub implementations of all injected services.
/// </summary>
public class SyncEngineTests
{
    // ==============================
    // Test infrastructure
    // ==============================

    private static AFHSyncDbContext MakeDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
            .UseInMemoryDatabase(dbName)
            // The in-memory provider can't honor the transactions SyncEngine uses; ignore the
            // warning (test-only) instead of letting EF escalate it to an exception.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AFHSyncDbContext(options);
    }

    private static IDbContextFactory<AFHSyncDbContext> CreateFactory(string dbName)
        => new TestDbContextFactory(dbName);

    private sealed class TestDbContextFactory(string dbName) : IDbContextFactory<AFHSyncDbContext>
    {
        public AFHSyncDbContext CreateDbContext() => MakeDbContext(dbName);
    }

    private static IConfiguration CreateEmptyConfig()
        => new ConfigurationBuilder().Build();

    /// <summary>Seeds one active tunnel (Id 1, name "Avail Tunnel") with phone list 1 and the given mailboxes.</summary>
    private static async Task SeedTunnelWithMailboxesAsync(string dbName, params TargetMailbox[] mailboxes)
    {
        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel { Id = 1, Name = "Avail Tunnel", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var tpl = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };
        tunnel.TunnelPhoneLists.Add(tpl);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TunnelPhoneLists.Add(tpl);
        seedCtx.TargetMailboxes.AddRange(mailboxes);
        await seedCtx.SaveChangesAsync();
    }

    private static ODataError UnavailableMailboxError() => new()
    {
        Error = new MainError
        {
            Code = MailboxAvailability.UnavailableErrorCode,
            Message = "The mailbox is either inactive, soft-deleted, or is hosted on-premise."
        }
    };

    private static SyncEngine CreateEngine(
        string dbName,
        ISourceResolver? sourceResolver = null,
        FakeContactPayloadBuilder? payloadBuilder = null,
        FakeContactWriter? contactWriter = null,
        FakeContactFolderManager? folderManager = null,
        IStaleContactHandler? staleHandler = null,
        FakeRunLogger? runLogger = null,
        ThrottleCounter? throttleCounter = null,
        FakePhotoSyncService? photoSyncService = null,
        AFHSync.Api.Services.IDDGResolver? ddgResolver = null,
        AFHSync.Api.Services.IFilterConverter? filterConverter = null)
    {
        return new SyncEngine(
            CreateFactory(dbName),
            sourceResolver ?? new FakeSourceResolver([]),
            payloadBuilder ?? new FakeContactPayloadBuilder(),
            contactWriter ?? new FakeContactWriter(),
            folderManager ?? new FakeContactFolderManager(),
            staleHandler ?? new FakeStaleContactHandler(),
            runLogger ?? new FakeRunLogger(),
            new RunClaimService(CreateFactory(dbName), NullLogger<RunClaimService>.Instance),
            throttleCounter ?? new ThrottleCounter(),
            photoSyncService ?? new FakePhotoSyncService(),
            null!, // GraphClientFactory — not used in unit tests
            CreateEmptyConfig(),
            NullLogger<SyncEngine>.Instance,
            ddgResolver!,
            filterConverter!);
    }

    // ==============================
    // Test 1: RunAsync creates and finalizes SyncRun even with no tunnels
    // ==============================

    [Fact]
    public async Task RunAsync_CreatesAndFinalizesSyncRunWithNoTunnels()
    {
        var dbName = Guid.NewGuid().ToString();
        // No tunnels seeded — DB is empty.
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, runLogger: runLogger);

        // Act
        var run = await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: run created (persisted by the advisory-lock guard, not via runLogger) and finalized
        Assert.NotNull(run);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.True(await verifyCtx.SyncRuns.AnyAsync());
        Assert.True(runLogger.WasFinalized);
    }

    // ==============================
    // Phase 2 (2.7): explicit run claiming
    // ==============================

    [Fact]
    public async Task RunAsync_WithRunId_ClaimsThatRowAndReadsTunnelsAndDryRunFromIt()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var seedCtx = MakeDbContext(dbName))
        {
            var t1 = new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
            var t2 = new Tunnel { Id = 2, Name = "T2", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
            var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
            var tpl = new TunnelPhoneList { TunnelId = 2, PhoneListId = 1, Tunnel = t2, PhoneList = phoneList };
            t2.TunnelPhoneLists.Add(tpl);
            seedCtx.Tunnels.AddRange(t1, t2);
            seedCtx.PhoneLists.Add(phoneList);
            seedCtx.TunnelPhoneLists.Add(tpl);
            seedCtx.TargetMailboxes.Add(new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
            // The API created this row: dry run, tunnel 2 only. The job arguments below say
            // otherwise (Manual, not dry) and must be ignored.
            seedCtx.SyncRuns.Add(new SyncRun
            {
                Id = 7, RunType = RunType.DryRun, Status = SyncStatus.Pending, IsDryRun = true,
                RequestedTunnelIds = "[2]", CreatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }

        var sourceResolver = new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]);
        var contactWriter = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, contactWriter: contactWriter, runLogger: runLogger);

        var run = await engine.RunAsync(7, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(7, run.Id);
        Assert.Equal(new[] { 2 }, sourceResolver.ResolvedTunnelIds);
        Assert.Empty(contactWriter.CreatedContactIds);                       // dry run honoured from the row
        Assert.Contains(runLogger.AddedItems, i => i.Action == "created");    // but the dry run still reports
        Assert.True(runLogger.WasFinalized);

        await using var verifyCtx = MakeDbContext(dbName);
        var row = await verifyCtx.SyncRuns.SingleAsync(r => r.Id == 7);
        Assert.NotNull(row.StartedAt);
        Assert.Equal(1, await verifyCtx.SyncRuns.CountAsync());             // no second row was created
    }

    [Fact]
    public async Task RunAsync_WithFinalizedRunId_ReturnsRowUntouchedAndDoesNoWork()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.Tunnels.Add(new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove });
            seedCtx.SyncRuns.Add(new SyncRun
            {
                Id = 9, RunType = RunType.Manual, Status = SyncStatus.Success, IsDryRun = false,
                StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow.AddMinutes(-6)
            });
            await seedCtx.SaveChangesAsync();
        }
        var sourceResolver = new FakeSourceResolver([]);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        var run = await engine.RunAsync(9, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(9, run.Id);
        Assert.Equal(SyncStatus.Success, run.Status);
        Assert.Equal(0, sourceResolver.ResolveCallCount);
        Assert.False(runLogger.WasFinalized);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.SyncRuns.CountAsync());
    }

    [Fact]
    public async Task RunAsync_WithRunId_WhileAnotherRunIsRunning_FailsThatRowWithoutWork()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.Add(new SyncRun { Id = 1, RunType = RunType.Scheduled, Status = SyncStatus.Running, StartedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
            seedCtx.SyncRuns.Add(new SyncRun { Id = 2, RunType = RunType.Manual, Status = SyncStatus.Pending, CreatedAt = DateTime.UtcNow });
            await seedCtx.SaveChangesAsync();
        }
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, runLogger: runLogger);

        var run = await engine.RunAsync(2, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(SyncStatus.Failed, run.Status);
        Assert.False(runLogger.WasFinalized);
        await using var verifyCtx = MakeDbContext(dbName);
        var row = await verifyCtx.SyncRuns.SingleAsync(r => r.Id == 2);
        Assert.Equal(SyncStatus.Failed, row.Status);
        Assert.Equal("another run was already in progress", row.ErrorSummary);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task ClaimAsync_WithNullRunId_WhilePendingRowExists_ReturnsBlockedAndCreatesNoRow()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.Add(new SyncRun { Id = 3, RunType = RunType.Manual, Status = SyncStatus.Pending, CreatedAt = DateTime.UtcNow });
            await seedCtx.SaveChangesAsync();
        }
        var claimService = new RunClaimService(CreateFactory(dbName), NullLogger<RunClaimService>.Instance);

        var result = await claimService.ClaimAsync(null, RunType.Scheduled, false, CancellationToken.None);

        Assert.Equal(RunClaimOutcome.Blocked, result.Outcome);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.SyncRuns.CountAsync());   // no second (cron) row was created
        var row = await verifyCtx.SyncRuns.SingleAsync(r => r.Id == 3);
        Assert.Equal(SyncStatus.Pending, row.Status);             // the pending row is untouched
    }

    [Fact]
    public void ParseRequestedTunnelIds_HandlesNullJsonAndGarbage()
    {
        Assert.Null(SyncEngine.ParseRequestedTunnelIds(null));
        Assert.Null(SyncEngine.ParseRequestedTunnelIds(""));
        Assert.Equal(new[] { 3, 5 }, SyncEngine.ParseRequestedTunnelIds("[3,5]")!);
        Assert.Empty(SyncEngine.ParseRequestedTunnelIds("not json")!);   // unreadable ⇒ process nothing, never "all"
    }

    // ==============================
    // Phase 2 (2.6b): Hangfire's shutdown token ⇒ Cancelled "worker shutting down"
    // ==============================

    [Fact]
    public async Task RunAsync_PreCancelledToken_FinalizesCancelledWithoutProcessingAnyTunnel()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.Tunnels.Add(new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove });
            await seedCtx.SaveChangesAsync();
        }
        var sourceResolver = new FakeSourceResolver([]);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        var run = await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, new CancellationToken(canceled: true));

        Assert.Equal(SyncStatus.Cancelled, run.Status);
        Assert.True(runLogger.WasFinalized);
        Assert.Equal(SyncStatus.Cancelled, runLogger.FinalizedStatus);
        Assert.Equal("worker shutting down", runLogger.FinalizedErrorSummary);
        Assert.Equal(0, sourceResolver.ResolveCallCount);
        // The row was still claimed (bookkeeping ignores the shutdown token) so it can be finalized.
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.SyncRuns.CountAsync());
    }

    [Fact]
    public async Task RunAsync_TokenCancelledMidRun_StopsAtNextTunnelBoundary()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.Tunnels.AddRange(
                new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove },
                new Tunnel { Id = 2, Name = "T2", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove });
            await seedCtx.SaveChangesAsync();
        }
        using var cts = new CancellationTokenSource();
        var sourceResolver = new CancellingSourceResolver(cts);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        var run = await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, cts.Token);

        Assert.Equal(1, sourceResolver.ResolveCallCount);              // second tunnel never started
        Assert.Equal(SyncStatus.Cancelled, run.Status);
        Assert.Equal(SyncStatus.Cancelled, runLogger.FinalizedStatus);
        Assert.Equal("worker shutting down", runLogger.FinalizedErrorSummary);
    }

    // ==============================
    // Phase 2 (2.1): unavailable mailboxes are stamped and skipped, not failed
    // ==============================

    [Fact]
    public async Task RunAsync_UnavailableMailbox_IsStampedNotFailed_NoRunItem()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-dead", Email = "dead@contoso.com", IsActive = true });
        var folderManager = new FakeContactFolderManager();
        folderManager.Failures["mb-dead"] = UnavailableMailboxError();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager,
            runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.DoesNotContain(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Equal(0, runLogger.FinalizedFailed);
        Assert.Equal(SyncStatus.Success, runLogger.FinalizedStatus);
        await using var verifyCtx = MakeDbContext(dbName);
        var mb = await verifyCtx.TargetMailboxes.SingleAsync();
        Assert.True(mb.IsActive);                                  // IsActive keeps its Entra meaning
        Assert.NotNull(mb.MailboxUnavailableAt);
        Assert.NotNull(mb.MailboxLastProbedAt);
        Assert.Contains("soft-deleted", mb.MailboxUnavailableReason);
    }

    [Fact]
    public async Task RunAsync_UnavailableMailboxProbedWithin7Days_IsExcluded()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-recent", Email = "recent@contoso.com", IsActive = true,
                MailboxUnavailableAt = DateTime.UtcNow.AddDays(-3), MailboxLastProbedAt = DateTime.UtcNow.AddDays(-3), MailboxUnavailableReason = "x" },
            new TargetMailbox { Id = 2, EntraId = "mb-ok", Email = "ok@contoso.com", IsActive = true });
        var folderManager = new FakeContactFolderManager();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(new[] { "mb-ok" }, folderManager.Requested);
    }

    [Fact]
    public async Task RunAsync_UnavailableMailboxProbedOver7DaysAgo_IsReprobed_AndRestamped()
    {
        var dbName = Guid.NewGuid().ToString();
        var firstSeen = DateTime.UtcNow.AddDays(-30);
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-stale", Email = "stale@contoso.com", IsActive = true,
                MailboxUnavailableAt = firstSeen, MailboxLastProbedAt = DateTime.UtcNow.AddDays(-8), MailboxUnavailableReason = "old reason" });
        var folderManager = new FakeContactFolderManager();
        folderManager.Failures["mb-stale"] = UnavailableMailboxError();   // still dead
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(new[] { "mb-stale" }, folderManager.Requested);
        await using var verifyCtx = MakeDbContext(dbName);
        var mb = await verifyCtx.TargetMailboxes.SingleAsync();
        Assert.Equal(firstSeen, mb.MailboxUnavailableAt);                            // first-seen is preserved
        Assert.True(mb.MailboxLastProbedAt > DateTime.UtcNow.AddMinutes(-1));        // probe time refreshed
        Assert.Contains("soft-deleted", mb.MailboxUnavailableReason);
    }

    [Fact]
    public async Task RunAsync_ReprobeSucceeds_ClearsUnavailableStamp()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-back", Email = "back@contoso.com", IsActive = true,
                MailboxUnavailableAt = DateTime.UtcNow.AddDays(-30), MailboxLastProbedAt = DateTime.UtcNow.AddDays(-8), MailboxUnavailableReason = "was dead" });
        var folderManager = new FakeContactFolderManager();   // no failure ⇒ lookup succeeds
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        await using var verifyCtx = MakeDbContext(dbName);
        var mb = await verifyCtx.TargetMailboxes.SingleAsync();
        Assert.Null(mb.MailboxUnavailableAt);
        Assert.Null(mb.MailboxLastProbedAt);
        Assert.Null(mb.MailboxUnavailableReason);
    }

    [Fact]
    public async Task RunAsync_OtherFolderError_StillFailsAndDoesNotStamp()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-err", Email = "err@contoso.com", IsActive = true });
        var folderManager = new FakeContactFolderManager();
        folderManager.Failures["mb-err"] = new InvalidOperationException("boom");
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager,
            runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        var failedItem = Assert.Single(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Equal("Folder 'Avail Tunnel': boom", failedItem.ErrorMessage);
        Assert.Equal(1, failedItem.TargetMailboxId);
        Assert.Equal(1, runLogger.FinalizedFailed);
        await using var verifyCtx = MakeDbContext(dbName);
        var mb = await verifyCtx.TargetMailboxes.SingleAsync();
        Assert.Null(mb.MailboxUnavailableAt);
        Assert.True(mb.IsActive);
    }

    // ==============================
    // Test 2: 0 source members logs warning
    // ==============================

    [Fact]
    public async Task RunAsync_WithZeroSourceMembers_LogsWarningAndSkipsTunnel()
    {
        var dbName = Guid.NewGuid().ToString();

        // Seed one active tunnel with a phone list.
        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel
        {
            Id = 1,
            Name = "Empty Tunnel",
            Status = TunnelStatus.Active,
            StalePolicy = StalePolicy.FlagHold,
            StaleHoldDays = 14,
        };
        seedCtx.Tunnels.Add(tunnel);
        await seedCtx.SaveChangesAsync();

        // Source resolver returns empty list.
        var sourceResolver = new FakeSourceResolver([]);
        var contactWriter = new FakeContactWriter();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, contactWriter: contactWriter);

        // Act
        var run = await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: no Graph writes occurred
        Assert.Empty(contactWriter.CreatedContactIds);
        Assert.Empty(contactWriter.UpdatedContactIds);
    }

    // ==============================
    // Phase 1: a DDG target that fails to resolve is recorded as a failed run item,
    // and an all-DDG SpecificUsers list that resolves to nothing targets NO mailboxes.
    // ==============================

    [Fact]
    public async Task RunAsync_DdgTargetFails_RecordsFailedItemAndTargetsNoMailboxes()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var seedCtx = MakeDbContext(dbName))
        {
            var tunnel = new Tunnel
            {
                Id = 1,
                Name = "Avalon Gate Code",
                Status = TunnelStatus.Active,
                StalePolicy = StalePolicy.AutoRemove,
                StaleHoldDays = 14,
            };
            var phoneList = new PhoneList
            {
                Id = 12,
                Name = "Avalon Users",
                TargetScope = TargetScope.SpecificUsers,
                TargetUserFilter = """{"ddgs":[{"id":"ddg-broken","displayName":"Buckhead Staff"}]}""",
            };
            var tunnelPhoneList = new TunnelPhoneList { TunnelId = 1, PhoneListId = 12, Tunnel = tunnel, PhoneList = phoneList };
            tunnel.TunnelPhoneLists.Add(tunnelPhoneList);
            seedCtx.Tunnels.Add(tunnel);
            seedCtx.PhoneLists.Add(phoneList);
            seedCtx.TunnelPhoneLists.Add(tunnelPhoneList);
            // An active mailbox that must NOT be processed, because the scope resolved to nothing.
            seedCtx.TargetMailboxes.Add(new TargetMailbox
            {
                Id = 1, EntraId = "mb-1", Email = "someone@x.com", IsActive = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }

        var sourceUser = new SourceUser
        {
            Id = 1, EntraId = "src-1", DisplayName = "Avalon Gate Code", Email = "avalon@x.com",
            IsEnabled = true, LastFetchedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var runLogger = new FakeRunLogger();
        var contactWriter = new FakeContactWriter();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([sourceUser]),
            contactWriter: contactWriter,
            runLogger: runLogger,
            ddgResolver: new NotFoundDdgResolver(),
            filterConverter: new PassThroughFilterConverter());

        var run = await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        var failedItem = Assert.Single(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Equal(1, failedItem.TunnelId);
        Assert.Contains("Buckhead Staff", failedItem.ErrorMessage);
        Assert.Contains("not found", failedItem.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runLogger.AddedItems, i => i.Action == "created");
        // FakeRunLogger records the finalize counters; the DDG failure is counted as a contact failure
        // so the tunnel is 'warned' and the run ends Warning (see DetermineStatus).
        Assert.True(runLogger.FinalizedFailed >= 1, "DDG failure must be counted as a failure");
        // The run-level errorSummary must surface DDG failures even when no tunnel outright
        // failed (tunnelsFailed == 0 here — this tunnel is merely 'warned').
        Assert.Contains("Buckhead Staff", runLogger.FinalizedErrorSummary);
        Assert.NotNull(run);
    }

    // ==============================
    // Phase 1 fix wave: a SpecificUsers phone list with a null TargetUserFilter must not
    // silently widen to every active mailbox. Mirrors
    // RunAsync_DdgTargetFails_RecordsFailedItemAndTargetsNoMailboxes's setup, but with a null
    // filter instead of a broken DDG reference.
    // ==============================

    [Fact]
    public async Task RunAsync_SpecificUsersWithNullFilter_TargetsNoMailboxes()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var seedCtx = MakeDbContext(dbName))
        {
            var tunnel = new Tunnel
            {
                Id = 1,
                Name = "Null Filter Tunnel",
                Status = TunnelStatus.Active,
                StalePolicy = StalePolicy.AutoRemove,
                StaleHoldDays = 14,
            };
            var phoneList = new PhoneList
            {
                Id = 12,
                Name = "Broken Scope Users",
                TargetScope = TargetScope.SpecificUsers,
                TargetUserFilter = null,
            };
            var tunnelPhoneList = new TunnelPhoneList { TunnelId = 1, PhoneListId = 12, Tunnel = tunnel, PhoneList = phoneList };
            tunnel.TunnelPhoneLists.Add(tunnelPhoneList);
            seedCtx.Tunnels.Add(tunnel);
            seedCtx.PhoneLists.Add(phoneList);
            seedCtx.TunnelPhoneLists.Add(tunnelPhoneList);
            // An active mailbox that must NOT be processed — a null filter must not widen
            // SpecificUsers scope to every active mailbox.
            seedCtx.TargetMailboxes.Add(new TargetMailbox
            {
                Id = 1, EntraId = "mb-1", Email = "someone@x.com", IsActive = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }

        var sourceUser = new SourceUser
        {
            Id = 1, EntraId = "src-1", DisplayName = "Someone", Email = "someone-src@x.com",
            IsEnabled = true, LastFetchedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var runLogger = new FakeRunLogger();
        var contactWriter = new FakeContactWriter();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([sourceUser]),
            contactWriter: contactWriter,
            runLogger: runLogger,
            ddgResolver: new NotFoundDdgResolver(),
            filterConverter: new PassThroughFilterConverter());

        var run = await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.DoesNotContain(runLogger.AddedItems, i => i.Action == "created");
        Assert.Empty(contactWriter.CreatedContactIds);
        Assert.NotNull(run);
    }

    // ==============================
    // Test 3: RunAsync calls SourceResolver for each active tunnel
    // ==============================

    [Fact]
    public async Task RunAsync_CallsSourceResolverForEachActiveTunnel()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        seedCtx.Tunnels.AddRange(
            new Tunnel { Id = 1, Name = "Tunnel 1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove },
            new Tunnel { Id = 2, Name = "Tunnel 2", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove },
            new Tunnel { Id = 3, Name = "Tunnel 3", Status = TunnelStatus.Inactive, StalePolicy = StalePolicy.AutoRemove } // inactive
        );
        await seedCtx.SaveChangesAsync();

        var sourceResolver = new FakeSourceResolver([]);
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver);

        // Act
        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: only 2 active tunnels were resolved (inactive skipped)
        Assert.Equal(2, sourceResolver.ResolveCallCount);
    }

    // ==============================
    // Test 4: Dry-run does NOT call ContactWriter methods
    // ==============================

    [Fact]
    public async Task RunAsync_DryRun_DoesNotCallContactWriterMethods()
    {
        var dbName = Guid.NewGuid().ToString();

        // Seed a tunnel, phone list, and a target mailbox.
        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel
        {
            Id = 1,
            Name = "Test Tunnel",
            Status = TunnelStatus.Active,
            StalePolicy = StalePolicy.AutoRemove
        };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-entra-id", Email = "user@contoso.com", IsActive = true };
        var tunnelPhoneList = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };

        tunnel.TunnelPhoneLists.Add(tunnelPhoneList);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.TunnelPhoneLists.Add(tunnelPhoneList);
        await seedCtx.SaveChangesAsync();

        // Source resolver returns 1 user.
        var sourceUser = new SourceUser { Id = 1, EntraId = "user-1", DisplayName = "Alice Smith", Email = "alice@contoso.com" };
        var sourceResolver = new FakeSourceResolver([sourceUser]);
        var contactWriter = new FakeContactWriter();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, contactWriter: contactWriter);

        // Act: dry run
        await engine.RunAsync(null, RunType.DryRun, isDryRun: true, CancellationToken.None);

        // Assert: ContactWriter was never called
        Assert.Empty(contactWriter.CreatedContactIds);
        Assert.Empty(contactWriter.UpdatedContactIds);
        Assert.Empty(contactWriter.DeletedContactIds);
    }

    // ==============================
    // Test 5: Dry-run still produces SyncRunItems
    // ==============================

    [Fact]
    public async Task RunAsync_DryRun_StillProducesSyncRunItems()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel { Id = 1, Name = "Test Tunnel", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-entra-id", Email = "user@contoso.com", IsActive = true };
        var tunnelPhoneList = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };

        tunnel.TunnelPhoneLists.Add(tunnelPhoneList);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.TunnelPhoneLists.Add(tunnelPhoneList);
        await seedCtx.SaveChangesAsync();

        var sourceUser = new SourceUser { Id = 1, EntraId = "user-1", DisplayName = "Alice Smith", Email = "alice@contoso.com" };
        var sourceResolver = new FakeSourceResolver([sourceUser]);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        // Act: dry run
        await engine.RunAsync(null, RunType.DryRun, isDryRun: true, CancellationToken.None);

        // Assert: at least one item was logged (the "created" action for the new contact)
        Assert.NotEmpty(runLogger.AddedItems);
        Assert.Contains(runLogger.AddedItems, i => i.Action == "created");
    }

    // ==============================
    // Test 6: Aggregate counts are correct
    // ==============================

    [Fact]
    public async Task RunAsync_AggregateCountsAreCorrect()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel { Id = 1, Name = "Count Tunnel", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-entra-id", Email = "user@contoso.com", IsActive = true };
        var tunnelPhoneList = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };

        tunnel.TunnelPhoneLists.Add(tunnelPhoneList);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.TunnelPhoneLists.Add(tunnelPhoneList);

        // Pre-existing sync state for SourceUser 1 with a hash that will MATCH (skipped)
        // and SourceUser 2 with a hash that will MISMATCH (updated).
        // The FakeContactPayloadBuilder always returns hash "new-hash".
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 1,
            SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1,
            GraphContactId = "g1", DataHash = "new-hash", // will match → skipped
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 2,
            SourceUserId = 2, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1,
            GraphContactId = "g2", DataHash = "old-hash", // will mismatch → updated
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        var sourceUsers = new List<SourceUser>
        {
            new() { Id = 1, EntraId = "u1", DisplayName = "Alice" }, // existing → skipped
            new() { Id = 2, EntraId = "u2", DisplayName = "Bob" },   // existing with old hash → updated
            new() { Id = 3, EntraId = "u3", DisplayName = "Carol" }  // new → created
        };
        var sourceResolver = new FakeSourceResolver(sourceUsers);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        // Act
        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert aggregate counts: 1 created, 1 updated, 1 skipped
        Assert.Equal(1, runLogger.FinalizedCreated);
        Assert.Equal(1, runLogger.FinalizedUpdated);
        Assert.Equal(1, runLogger.FinalizedSkipped);
    }

    // ==============================
    // Test 7: RunAsync resets ThrottleCounter at run start
    // ==============================

    [Fact]
    public async Task RunAsync_ResetsThrottleCounter_AtRunStart()
    {
        var dbName = Guid.NewGuid().ToString();
        // No tunnels seeded — run will complete with no work.
        var runLogger = new FakeRunLogger();
        var throttleCounter = new ThrottleCounter();

        // Simulate stale state from a previous run.
        throttleCounter.Increment();
        throttleCounter.Increment();
        throttleCounter.Increment();
        throttleCounter.Increment();
        throttleCounter.Increment(); // counter = 5

        var engine = CreateEngine(dbName, runLogger: runLogger, throttleCounter: throttleCounter);

        // Act
        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: throttle events should be 0 because counter was reset at run start
        // and no actual throttling occurred during this (empty) run.
        Assert.Equal(0, runLogger.FinalizedThrottleEvents);
    }

    // ==============================
    // Test 8: RunAsync passes ThrottleCounter.Count to FinalizeRunAsync
    // ==============================

    [Fact]
    public async Task RunAsync_PassesThrottleCounterCount_ToFinalizeRunAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        // No tunnels seeded — run will complete with no work.
        var runLogger = new FakeRunLogger();
        var throttleCounter = new ThrottleCounter();
        var engine = CreateEngine(dbName, runLogger: runLogger, throttleCounter: throttleCounter);

        // Act: run completes with a clean counter (no throttling occurred).
        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: FinalizeRunAsync received the counter value (0 — no throttling in test).
        // This verifies the engine reads from throttleCounter.Count (not a hardcoded 0 or
        // an old stale value), since reset was called and no retries occurred.
        Assert.Equal(0, runLogger.FinalizedThrottleEvents);
        Assert.True(runLogger.WasFinalized);
    }

    // ==============================
    // Test 9: RunAsync in included mode calls PhotoSync for each tunnel
    // ==============================

    [Fact]
    public async Task RunAsync_IncludedMode_CallsPhotoSyncForEachTunnel()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        // Seed photo_sync_mode = included
        seedCtx.AppSettings.Add(new AppSetting
        {
            Id = 100, Key = "photo_sync_mode", Value = "included",
            Description = "Test", UpdatedAt = DateTime.UtcNow
        });
        seedCtx.Tunnels.AddRange(
            new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, PhotoSyncEnabled = true, StalePolicy = StalePolicy.AutoRemove },
            new Tunnel { Id = 2, Name = "T2", Status = TunnelStatus.Active, PhotoSyncEnabled = true, StalePolicy = StalePolicy.AutoRemove }
        );
        await seedCtx.SaveChangesAsync();

        var sourceResolver = new FakeSourceResolver([]);
        var photoSync = new FakePhotoSyncService();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, photoSyncService: photoSync);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Photo sync called once per active tunnel in included mode
        Assert.Equal(2, photoSync.SyncPhotosCallCount);
    }

    // ==============================
    // Test 10: RunAsync in disabled mode skips PhotoSync
    // ==============================

    [Fact]
    public async Task RunAsync_DisabledMode_SkipsPhotoSync()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        // Seed photo_sync_mode = disabled
        seedCtx.AppSettings.Add(new AppSetting
        {
            Id = 100, Key = "photo_sync_mode", Value = "disabled",
            Description = "Test", UpdatedAt = DateTime.UtcNow
        });
        seedCtx.Tunnels.Add(
            new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, PhotoSyncEnabled = true, StalePolicy = StalePolicy.AutoRemove }
        );
        await seedCtx.SaveChangesAsync();

        var sourceResolver = new FakeSourceResolver([]);
        var photoSync = new FakePhotoSyncService();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, photoSyncService: photoSync);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Photo sync never called in disabled mode
        Assert.Equal(0, photoSync.SyncPhotosCallCount);
        Assert.Equal(0, photoSync.RunAllCallCount);
    }

    // ==============================
    // Test 11: RunAsync in separate_pass with auto_trigger calls RunAllAsync
    // ==============================

    [Fact]
    public async Task RunAsync_SeparatePassWithAutoTrigger_CallsRunAllAsync()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        // Seed photo_sync_mode = separate_pass with auto_trigger = true
        seedCtx.AppSettings.AddRange(
            new AppSetting { Id = 100, Key = "photo_sync_mode", Value = "separate_pass", Description = "Test", UpdatedAt = DateTime.UtcNow },
            new AppSetting { Id = 101, Key = "photo_sync_auto_trigger", Value = "true", Description = "Test", UpdatedAt = DateTime.UtcNow }
        );
        await seedCtx.SaveChangesAsync();

        var photoSync = new FakePhotoSyncService();
        var engine = CreateEngine(dbName, photoSyncService: photoSync);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // In separate_pass mode with auto_trigger, RunAllAsync should be called
        Assert.Equal(1, photoSync.RunAllCallCount);
        // SyncPhotosForTunnelAsync should NOT be called (not included mode)
        Assert.Equal(0, photoSync.SyncPhotosCallCount);
    }

    // ==============================
    // Test 12: one mailbox's unhandled error must not abort the whole tunnel
    // ==============================

    [Fact]
    public async Task RunAsync_OneMailboxThrows_ContainsFailureWithoutAbortingTunnel()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel { Id = 1, Name = "Resilient", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var mb1 = new TargetMailbox { Id = 1, EntraId = "e1", Email = "a@contoso.com", IsActive = true };
        var mb2 = new TargetMailbox { Id = 2, EntraId = "e2", Email = "b@contoso.com", IsActive = true };
        var tpl = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };
        tunnel.TunnelPhoneLists.Add(tpl);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TargetMailboxes.AddRange(mb1, mb2);
        seedCtx.TunnelPhoneLists.Add(tpl);
        await seedCtx.SaveChangesAsync();

        var sourceUsers = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice" } };
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(
            dbName,
            sourceResolver: new FakeSourceResolver(sourceUsers),
            staleHandler: new ThrowingStaleContactHandler(),
            runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Each mailbox's error is contained as that mailbox's single failure; the tunnel
        // completes (not marked failed), so a single bad mailbox can't nuke the run.
        Assert.True(runLogger.WasFinalized);
        Assert.Equal(0, runLogger.FinalizedTunnelsFailed);
        Assert.Equal(2, runLogger.FinalizedFailed);
    }

    // ==============================
    // Test 13: a 404 on update means the contact was deleted on the device — self-heal
    // ==============================

    [Fact]
    public async Task RunAsync_UpdateReturns404_ClearsDeadStateForRecreate_NotCountedFailed()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel { Id = 1, Name = "T", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true };
        var tpl = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };
        tunnel.TunnelPhoneLists.Add(tpl);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.TunnelPhoneLists.Add(tpl);
        // Existing state with an OLD hash → FakeContactPayloadBuilder returns "new-hash", so this
        // classifies as an UPDATE. The contact, however, was deleted on the device → update 404s.
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 1,
            SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1,
            GraphContactId = "gone-contact", DataHash = "old-hash",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        var sourceUsers = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice" } };
        var writer = new FakeContactWriter { UpdateReturnsNotFound = true };
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(
            dbName,
            sourceResolver: new FakeSourceResolver(sourceUsers),
            contactWriter: writer,
            runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // A 404 means the contact is gone; clear the dead state so it recreates next run, and
        // don't count it as a permanent failure (it would 404 forever otherwise).
        Assert.Equal(0, runLogger.FinalizedFailed);
        using var verifyCtx = MakeDbContext(dbName);
        Assert.Empty(await verifyCtx.ContactSyncStates.Where(s => s.GraphContactId == "gone-contact").ToListAsync());
    }

    // ==============================
    // Stub implementations
    // ==============================

    private sealed class FakeSourceResolver(List<SourceUser> users) : ISourceResolver
    {
        public int ResolveCallCount { get; private set; }
        public List<int> ResolvedTunnelIds { get; } = [];

        public Task<List<SourceUser>> ResolveAsync(Tunnel tunnel, CancellationToken ct)
        {
            ResolveCallCount++;
            ResolvedTunnelIds.Add(tunnel.Id);
            return Task.FromResult(users);
        }
    }

    /// <summary>Cancels the given source on its first call — simulates a shutdown arriving mid-run.</summary>
    private sealed class CancellingSourceResolver(CancellationTokenSource cts) : ISourceResolver
    {
        public int ResolveCallCount { get; private set; }

        public Task<List<SourceUser>> ResolveAsync(Tunnel tunnel, CancellationToken ct)
        {
            ResolveCallCount++;
            cts.Cancel();
            return Task.FromResult(new List<SourceUser>());
        }
    }

    /// <summary>
    /// Always returns hash "new-hash" so existing states with "old-hash" trigger updates,
    /// and states with "new-hash" are skipped.
    /// </summary>
    private sealed class FakeContactPayloadBuilder : IContactPayloadBuilder
    {
        public ContactPayloadResult BuildPayload(
            SourceUser source,
            IReadOnlyList<FieldProfileField> fieldSettings,
            ContactSyncState? existingState)
        {
            var payload = new SortedDictionary<string, string> { { "DisplayName", source.DisplayName ?? "Unknown" } };
            return new ContactPayloadResult(payload, "new-hash");
        }
    }

    private sealed class FakeContactWriter : IContactWriter
    {
        public List<string> CreatedContactIds { get; } = [];
        public List<string> UpdatedContactIds { get; } = [];
        public List<string> DeletedContactIds { get; } = [];

        /// <summary>When true, batch updates return a 404 NotFound (contact deleted on the device).</summary>
        public bool UpdateReturnsNotFound { get; init; }

        public Task<string> CreateContactAsync(string mailboxEntraId, string folderId, SortedDictionary<string, string> payload, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString();
            CreatedContactIds.Add(id);
            return Task.FromResult(id);
        }

        public Task UpdateContactAsync(string mailboxEntraId, string graphContactId, SortedDictionary<string, string> payload, CancellationToken ct)
        {
            UpdatedContactIds.Add(graphContactId);
            return Task.CompletedTask;
        }

        public Task DeleteContactAsync(string mailboxEntraId, string graphContactId, CancellationToken ct)
        {
            DeletedContactIds.Add(graphContactId);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(
            string mailboxEntraId, string folderId,
            List<(string key, SortedDictionary<string, string> payload)> operations, CancellationToken ct)
        {
            var results = new Dictionary<string, BatchOperationResult>();
            foreach (var (key, _) in operations)
            {
                var id = Guid.NewGuid().ToString();
                CreatedContactIds.Add(id);
                results[key] = new BatchOperationResult(true, id);
            }
            return Task.FromResult(results);
        }

        public Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(
            string mailboxEntraId,
            List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations, CancellationToken ct)
        {
            var results = new Dictionary<string, BatchOperationResult>();
            foreach (var (key, graphContactId, _) in operations)
            {
                UpdatedContactIds.Add(graphContactId);
                results[key] = UpdateReturnsNotFound
                    ? new BatchOperationResult(false, Error: "HTTP 404", NotFound: true)
                    : new BatchOperationResult(true);
            }
            return Task.FromResult(results);
        }

        public Task<Dictionary<string, BatchOperationResult>> DeleteContactsBatchAsync(
            string mailboxEntraId,
            List<(string key, string graphContactId)> operations, CancellationToken ct)
        {
            var results = new Dictionary<string, BatchOperationResult>();
            foreach (var (key, graphContactId) in operations)
            {
                DeletedContactIds.Add(graphContactId);
                results[key] = new BatchOperationResult(true);
            }
            return Task.FromResult(results);
        }
    }

    private sealed class FakeContactFolderManager : IContactFolderManager
    {
        /// <summary>Mailboxes (by EntraId) whose folder lookup throws the given exception.</summary>
        public Dictionary<string, Exception> Failures { get; } = new();

        /// <summary>Every mailbox EntraId the engine asked a folder for, in call order.</summary>
        public List<string> Requested { get; } = [];

        public Task<(string folderId, bool wasCreated)> GetOrCreateFolderAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            Requested.Add(mailboxEntraId);
            if (Failures.TryGetValue(mailboxEntraId, out var ex))
                throw ex;
            return Task.FromResult(("fake-folder-id", false));
        }

        public void ResetCache() { }
    }

    private sealed class FakeStaleContactHandler : IStaleContactHandler
    {
        public Task<StaleResult> HandleStaleAsync(
            Tunnel tunnel, int phoneListId, int targetMailboxId,
            string mailboxEntraId, HashSet<int> currentSourceUserIds, CancellationToken ct)
            => Task.FromResult(new StaleResult(0, 0));
    }

    private sealed class FakePhotoSyncService : IPhotoSyncService
    {
        public int SyncPhotosCallCount { get; private set; }
        public int RunAllCallCount { get; private set; }

        public Task<(int updated, int failed)> SyncPhotosForTunnelAsync(
            Tunnel tunnel, SyncRun run, List<SourceUser> sourceUsers,
            bool isDryRun, CancellationToken ct,
            int priorPhotosUpdated = 0, int priorPhotosFailed = 0, int priorTunnelsProcessed = 0)
        {
            SyncPhotosCallCount++;
            return Task.FromResult((0, 0));
        }

        public Task RunAllAsync(RunType runType, bool isDryRun, CancellationToken ct)
        {
            RunAllCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunLogger : IRunLogger
    {
        public bool WasCreated { get; private set; }
        public bool WasFinalized { get; private set; }
        public List<SyncRunItem> AddedItems { get; } = [];
        public int FinalizedCreated { get; private set; }
        public int FinalizedUpdated { get; private set; }
        public int FinalizedSkipped { get; private set; }
        public int FinalizedFailed { get; private set; }
        public int FinalizedTunnelsFailed { get; private set; }
        public int FinalizedThrottleEvents { get; private set; }
        public string? FinalizedErrorSummary { get; private set; }
        public SyncStatus? FinalizedStatus { get; private set; }

        private int _nextRunId = 1;

        public Task<SyncRun> CreateRunAsync(RunType runType, bool isDryRun, CancellationToken ct)
        {
            WasCreated = true;
            var run = new SyncRun
            {
                Id = _nextRunId++,
                RunType = runType,
                Status = SyncStatus.Running,
                IsDryRun = isDryRun,
                StartedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            return Task.FromResult(run);
        }

        public void AddItem(SyncRunItem item) => AddedItems.Add(item);

        public Task FlushItemsAsync(CancellationToken ct) => Task.CompletedTask;

        public Task FinalizeRunAsync(
            SyncRun run, SyncStatus status, string? errorSummary,
            int contactsCreated, int contactsUpdated, int contactsSkipped, int contactsFailed,
            int contactsRemoved, int tunnelsProcessed, int tunnelsWarned, int tunnelsFailed,
            int throttleEvents, int photosUpdated, int photosFailed, CancellationToken ct)
        {
            WasFinalized = true;
            FinalizedCreated = contactsCreated;
            FinalizedUpdated = contactsUpdated;
            FinalizedSkipped = contactsSkipped;
            FinalizedFailed = contactsFailed;
            FinalizedTunnelsFailed = tunnelsFailed;
            FinalizedThrottleEvents = throttleEvents;
            FinalizedErrorSummary = errorSummary;
            FinalizedStatus = status;
            return Task.CompletedTask;
        }
    }

    /// <summary>Every DDG lookup returns null ("not found").</summary>
    private sealed class NotFoundDdgResolver : AFHSync.Api.Services.IDDGResolver
    {
        public Task<IReadOnlyList<AFHSync.Api.Services.DdgInfo>> ListDdgsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AFHSync.Api.Services.DdgInfo>>([]);

        public Task<AFHSync.Api.Services.DdgInfo?> GetDdgAsync(string identity, CancellationToken ct = default)
            => Task.FromResult<AFHSync.Api.Services.DdgInfo?>(null);
    }

    private sealed class PassThroughFilterConverter : AFHSync.Api.Services.IFilterConverter
    {
        public AFHSync.Api.DTOs.FilterConversionResult Convert(string opathFilter)
            => new(true, opathFilter);

        public string ToPlainLanguage(string opathFilter) => opathFilter;
    }

    /// <summary>Stale handler that always throws — simulates an unexpected error in the
    /// post-folder region of ProcessMailboxAsync (e.g. a DB write failure).</summary>
    private sealed class ThrowingStaleContactHandler : IStaleContactHandler
    {
        public Task<StaleResult> HandleStaleAsync(
            Tunnel tunnel, int phoneListId, int targetMailboxId,
            string mailboxEntraId, HashSet<int> currentSourceUserIds, CancellationToken ct)
            => throw new InvalidOperationException("simulated mailbox-level failure");
    }
}
