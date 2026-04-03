namespace AFHSync.Shared.Entities;

public class SourceUser
{
    public int Id { get; set; }
    public string EntraId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? BusinessPhone { get; set; }
    public string? MobilePhone { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? OfficeLocation { get; set; }
    public string? CompanyName { get; set; }
    public string? StreetAddress { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Notes { get; set; }
    public string? PhotoHash { get; set; }
    public string? ExtensionAttr1 { get; set; }
    public string? ExtensionAttr2 { get; set; }
    public string? ExtensionAttr3 { get; set; }
    public string? ExtensionAttr4 { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? MailboxType { get; set; }
    public DateTime? LastFetchedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
