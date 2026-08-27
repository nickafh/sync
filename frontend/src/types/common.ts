// Status enums used across pages
export type TunnelStatus = 'active' | 'inactive';
export type SyncRunStatus = 'pending' | 'running' | 'success' | 'warning' | 'failed' | 'cancelled';
export type SyncRunType = 'scheduled' | 'manual' | 'dry_run';
export type StalePolicy = 'auto_remove' | 'flag_hold' | 'leave';
export type FieldBehavior = 'always' | 'add_missing' | 'nosync' | 'remove_blank';
export type SyncItemAction = 'created' | 'updated' | 'skipped' | 'failed' | 'removed' | 'stale_detected' | 'photo_updated';

/** Phase 3 (§3.3): paged envelope returned by /sync-runs, /sync-runs/{id}/items and /phone-lists/{id}/contacts. */
export interface PagedResult<T> {
  items: T[];
  hasMore: boolean;
  /** Only present on endpoints with a cheap count (phone-list contacts). */
  total?: number | null;
}
