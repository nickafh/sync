namespace AFHSync.Api.DTOs;

/// <summary>Phase 2 (§2.1): target mailboxes the worker is currently skipping because Graph reports no REST-enabled mailbox.</summary>
public record UnavailableMailboxesDto(
    int TotalActive,
    int Unavailable,
    IReadOnlyList<UnavailableMailboxDto> Items);

public record UnavailableMailboxDto(
    int Id,
    string? DisplayName,
    string Email,
    DateTime UnavailableSince,
    DateTime? LastCheckedAt,
    string? Reason);
