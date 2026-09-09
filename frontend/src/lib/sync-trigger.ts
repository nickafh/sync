import type { TriggerSyncRequest } from '@/types/sync-run';

/** §5.3: the dashboard's "Run Sync Now" request. Audit is opt-in; a manual run is never a dry run here. */
export function buildManualTriggerRequest(auditFolders: boolean): TriggerSyncRequest {
  return { runType: 'manual', isDryRun: false, tunnelIds: null, auditFolders };
}
