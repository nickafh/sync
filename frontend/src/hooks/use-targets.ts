'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

export function useUnavailableMailboxes() {
  return useQuery({
    queryKey: ['targets', 'unavailable'],
    queryFn: () => api.targets.unavailable(),
    staleTime: 60 * 1000,
  });
}
