'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { TriggerSyncRequest } from '@/types/sync-run';
import type { SyncRunDto } from '@/types/sync-run';

export function useDashboard() {
  return useQuery({
    queryKey: ['dashboard'],
    queryFn: () => api.dashboard.get(),
    staleTime: 30 * 1000,
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
