import type { SyncRunStatus, SyncRunType, SyncItemAction } from './common';

export interface SyncRunDto {
  id: number;
  runType: SyncRunType;
  status: SyncRunStatus;
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
  photosFailed: number;
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
  photosFailed: number;
  errors: string[];
}

export interface SyncRunDetailDto {
  id: number;
  runType: SyncRunType;
  status: SyncRunStatus;
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
  photosFailed: number;
  throttleEvents: number;
  errorSummary: string | null;
  tunnelSummaries: TunnelRunSummaryDto[];
}

export interface SyncRunItemDto {
  id: number;
  tunnelId: number | null;
  sourceUserId: number | null;
  action: SyncItemAction;
  fieldChanges: string | null;
  errorMessage: string | null;
  createdAt: string;
}

export interface TriggerSyncRequest {
  runType: SyncRunType;
  isDryRun: boolean;
  tunnelIds: number[] | null;
}
