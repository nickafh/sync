export interface UnavailableMailboxDto {
  id: number;
  displayName: string | null;
  email: string;
  unavailableSince: string;
  lastCheckedAt: string | null;
  reason: string | null;
}

export interface UnavailableMailboxesDto {
  totalActive: number;
  unavailable: number;
  items: UnavailableMailboxDto[];
}
