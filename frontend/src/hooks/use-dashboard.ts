'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { TriggerSyncRequest } from '@/types/sync-run';
import type { SyncRunDto } from '@/types/sync-run';

/** Dashboard poll cadence while a run is active vs idle. The idle poll is what lets the page
 *  notice runs it did not start — the photo sync chained after a contact run finalizes, and
 *  scheduled runs — without a manual refresh (page.tsx adopts any running/pending run it sees). */
export const DASHBOARD_POLL_ACTIVE_MS = 3000;
export const DASHBOARD_POLL_IDLE_MS = 15000;

export function useDashboard(isSyncing = false) {
  return useQuery({
    queryKey: ['dashboard'],
    queryFn: () => api.dashboard.get(),
    staleTime: 30 * 1000,
    refetchInterval: isSyncing ? DASHBOARD_POLL_ACTIVE_MS : DASHBOARD_POLL_IDLE_MS,
  });
}

export function useTriggerSync() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (req: TriggerSyncRequest) => api.syncRuns.trigger(req),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      queryClient.invalidateQueries({ queryKey: ['sync-runs'] });
    },
  });
}

export function useSyncRunPolling(runId: number | null) {
  return useQuery({
    queryKey: ['sync-run', runId],
    queryFn: () => api.syncRuns.get(runId!),
    enabled: runId !== null && runId > 0,
    refetchInterval: (query) => {
      const data = query.state.data as SyncRunDto | undefined;
      return data?.status === 'running' || data?.status === 'pending' ? 3000 : false;
    },
  });
}
