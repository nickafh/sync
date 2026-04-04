export interface SyncRunDto {
  id: number;
  runType: string;
  status: string;
  isDryRun: boolean;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
  contactsCreated: number;
  contactsUpdated: number;
  contactsRemoved: number;
  contactsSkipped: number;
  contactsFailed: number;
  photosUpdated: number;
  throttleEvents: number;
}

export interface TunnelRunSummaryDto {
  tunnelId: number | null;
  tunnelName: string;
  contactsCreated: number;
  contactsUpdated: number;
  contactsRemoved: number;
  contactsSkipped: number;
  contactsFailed: number;
  photosUpdated: number;
  errors: string[];
}

export interface SyncRunDetailDto {
  id: number;
  runType: string;
  status: string;
  isDryRun: boolean;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
  tunnelsProcessed: number;
  tunnelsWarned: number;
  contactsCreated: number;
  contactsUpdated: number;
  contactsRemoved: number;
  contactsSkipped: number;
  contactsFailed: number;
  photosUpdated: number;
  throttleEvents: number;
  errorSummary: string | null;
  tunnelSummaries: TunnelRunSummaryDto[];
}

export interface SyncRunItemDto {
  id: number;
  tunnelId: number | null;
  sourceUserId: number | null;
  action: string;
  fieldChanges: string | null;
  errorMessage: string | null;
  createdAt: string;
}

export interface TriggerSyncRequest {
  runType: string;
  isDryRun: boolean;
  tunnelIds: number[] | null;
}
