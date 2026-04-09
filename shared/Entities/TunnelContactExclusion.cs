namespace AFHSync.Shared.Entities;

public class TunnelContactExclusion
{
    public int Id { get; set; }
    public int TunnelId { get; set; }
    public string EntraId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Tunnel Tunnel { get; set; } = null!;
}
