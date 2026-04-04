'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { UpdateFieldProfileRequest } from '@/types/field-profile';

export function useFieldProfiles() {
  return useQuery({
    queryKey: ['field-profiles'],
    queryFn: () => api.fieldProfiles.list(),
    staleTime: 5 * 60 * 1000,
  });
}

export function useFieldProfile(id: number) {
  return useQuery({
    queryKey: ['field-profile', id],
    queryFn: () => api.fieldProfiles.get(id),
    staleTime: 5 * 60 * 1000,
    enabled: id > 0,
  });
}

export function useUpdateFieldProfile() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ id, data }: { id: number; data: UpdateFieldProfileRequest }) =>
      api.fieldProfiles.update(id, data),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['field-profile', variables.id] });
      queryClient.invalidateQueries({ queryKey: ['field-profiles'] });
    },
  });
}
