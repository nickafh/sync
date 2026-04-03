namespace AFHSync.Shared.Entities;

public class FieldProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<FieldProfileField> FieldProfileFields { get; set; } = [];
    public ICollection<Tunnel> Tunnels { get; set; } = [];
}
