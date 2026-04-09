namespace AFHSync.Shared.Entities;

public class OrgContactFilter
{
    public int Id { get; set; }
    public int TunnelId { get; set; }
    public string OrgContactId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? CompanyName { get; set; }
    public bool IsExcluded { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public Tunnel Tunnel { get; set; } = null!;
}
