'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

export function usePhoneLists() {
  return useQuery({
    queryKey: ['phone-lists'],
    queryFn: () => api.phoneLists.list(),
    staleTime: 5 * 60 * 1000,
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
