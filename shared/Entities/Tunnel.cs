using AFHSync.Shared.Enums;

namespace AFHSync.Shared.Entities;

public class Tunnel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? FieldProfileId { get; set; }
    public StalePolicy StalePolicy { get; set; } = StalePolicy.FlagHold;
    public int StaleHoldDays { get; set; } = 14;
    public bool PhotoSyncEnabled { get; set; } = true;
    public string? TargetGroupId { get; set; }
    public string? TargetGroupName { get; set; }
    public string? TargetUserEmails { get; set; }
    public TunnelStatus Status { get; set; } = TunnelStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public FieldProfile? FieldProfile { get; set; }
    public ICollection<TunnelSource> TunnelSources { get; set; } = [];
    public ICollection<TunnelPhoneList> TunnelPhoneLists { get; set; } = [];
}
