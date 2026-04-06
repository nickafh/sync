using AFHSync.Shared.Enums;

namespace AFHSync.Shared.Entities;

public class TunnelSource
{
    public int Id { get; set; }
    public int TunnelId { get; set; }
    public SourceType SourceType { get; set; } = SourceType.Ddg;
    public string SourceIdentifier { get; set; } = string.Empty;
    public string? SourceDisplayName { get; set; }
    public string? SourceSmtpAddress { get; set; }
    public string? SourceFilterPlain { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public Tunnel Tunnel { get; set; } = null!;
}
