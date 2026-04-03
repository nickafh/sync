using AFHSync.Shared.Enums;

namespace AFHSync.Shared.Entities;

public class FieldProfileField
{
    public int Id { get; set; }
    public int FieldProfileId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string FieldSection { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public SyncBehavior Behavior { get; set; } = SyncBehavior.Always;
    public int DisplayOrder { get; set; }

    // Navigation properties
    public FieldProfile FieldProfile { get; set; } = null!;
}
