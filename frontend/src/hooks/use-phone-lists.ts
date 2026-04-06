'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { CreatePhoneListRequest } from '@/types/phone-list';

export function usePhoneLists() {
  return useQuery({
    queryKey: ['phone-lists'],
    queryFn: () => api.phoneLists.list(),
    staleTime: 5 * 60 * 1000,
  });
}

export function useCreatePhoneList() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (data: CreatePhoneListRequest) => api.phoneLists.create(data),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['phone-lists'] }),
  });
}

export function useUpdatePhoneList() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, data }: { id: number; data: CreatePhoneListRequest }) =>
      api.phoneLists.update(id, data),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['phone-lists'] }),
  });
}

export function useDeletePhoneList() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => api.phoneLists.delete(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['phone-lists'] }),
  });
}

export function usePhoneListContacts(id: number, page: number, pageSize: number) {
  return useQuery({
    queryKey: ['phone-list-contacts', id, page, pageSize],
    queryFn: () => api.phoneLists.getContacts(id, page, pageSize + 1),
    staleTime: 5 * 60 * 1000,
    enabled: id > 0,
  });
}
