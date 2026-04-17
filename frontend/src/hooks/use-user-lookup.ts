'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

export function useUserFolderState(email: string | null) {
  const trimmed = email?.trim() ?? '';
  return useQuery({
    queryKey: ['user-folder-state', trimmed.toLowerCase()],
    queryFn: () => api.users.folderState(trimmed),
    enabled: trimmed.length > 0 && trimmed.includes('@'),
    staleTime: 30 * 1000,
    retry: false,
  });
}
