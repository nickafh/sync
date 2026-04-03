namespace AFHSync.Shared.Entities;

public class TunnelPhoneList
{
    public int Id { get; set; }
    public int TunnelId { get; set; }
    public int PhoneListId { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public Tunnel Tunnel { get; set; } = null!;
    public PhoneList PhoneList { get; set; } = null!;
}
