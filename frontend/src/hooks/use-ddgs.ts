'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

export function useDdgs() {
  return useQuery({
    queryKey: ['ddgs'],
    queryFn: () => api.ddgs.list(),
    staleTime: 10 * 60 * 1000,
  });
}
