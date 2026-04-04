'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

export function useSyncRuns(page: number, pageSize: number) {
  return useQuery({
    queryKey: ['sync-runs', page, pageSize],
    queryFn: () => api.syncRuns.list(page, pageSize + 1),
    staleTime: 30 * 1000,
  });
}

export function useSyncRun(id: number) {
  return useQuery({
    queryKey: ['sync-run', id],
    queryFn: () => api.syncRuns.get(id),
    staleTime: Infinity,
    enabled: id > 0,
  });
}

export function useSyncRunItems(id: number, page: number, pageSize: number, action?: string) {
  return useQuery({
    queryKey: ['sync-run-items', id, page, pageSize, action],
    queryFn: () => api.syncRuns.getItems(id, page, pageSize + 1, action),
    staleTime: Infinity,
    enabled: id > 0,
  });
}
