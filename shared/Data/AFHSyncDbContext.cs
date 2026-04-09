using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Shared.Data;

public class AFHSyncDbContext : DbContext
{
    public AFHSyncDbContext(DbContextOptions<AFHSyncDbContext> options) : base(options) { }

    public DbSet<Tunnel> Tunnels => Set<Tunnel>();
    public DbSet<PhoneList> PhoneLists => Set<PhoneList>();
    public DbSet<TunnelSource> TunnelSources => Set<TunnelSource>();
    public DbSet<TunnelPhoneList> TunnelPhoneLists => Set<TunnelPhoneList>();
    public DbSet<FieldProfile> FieldProfiles => Set<FieldProfile>();
    public DbSet<FieldProfileField> FieldProfileFields => Set<FieldProfileField>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<SourceUser> SourceUsers => Set<SourceUser>();
    public DbSet<TargetMailbox> TargetMailboxes => Set<TargetMailbox>();
    public DbSet<ContactSyncState> ContactSyncStates => Set<ContactSyncState>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<SyncRunItem> SyncRunItems => Set<SyncRunItem>();
    public DbSet<OrgContactFilter> OrgContactFilters => Set<OrgContactFilter>();
    public DbSet<TunnelContactExclusion> TunnelContactExclusions => Set<TunnelContactExclusion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AFHSyncDbContext).Assembly);
    }
}
