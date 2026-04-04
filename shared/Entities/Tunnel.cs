using AFHSync.Shared.Enums;

namespace AFHSync.Shared.Entities;

public class Tunnel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public SourceType SourceType { get; set; } = SourceType.Ddg;
    public string SourceIdentifier { get; set; } = string.Empty;
    public string? SourceDisplayName { get; set; }
    public string? SourceSmtpAddress { get; set; }
    public string? SourceFilterPlain { get; set; }
    public TargetScope TargetScope { get; set; } = TargetScope.AllUsers;
    public string? TargetUserFilter { get; set; }
    public int? FieldProfileId { get; set; }
    public StalePolicy StalePolicy { get; set; } = StalePolicy.FlagHold;
    public int StaleHoldDays { get; set; } = 14;
    public TunnelStatus Status { get; set; } = TunnelStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public FieldProfile? FieldProfile { get; set; }
    public ICollection<TunnelPhoneList> TunnelPhoneLists { get; set; } = [];
}
