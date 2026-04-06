using AFHSync.Shared.Enums;

namespace AFHSync.Shared.Entities;

public class PhoneList
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ExchangeFolderId { get; set; }
    public string? Description { get; set; }
    public int ContactCount { get; set; }
    public int UserCount { get; set; }
    public TargetScope TargetScope { get; set; } = TargetScope.AllUsers;
    public string? TargetUserFilter { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<TunnelPhoneList> TunnelPhoneLists { get; set; } = [];
}
