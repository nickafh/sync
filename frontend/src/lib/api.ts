import type { DashboardDto } from '@/types/dashboard';
import type { TunnelDto, TunnelDetailDto, UpdateTunnelRequest } from '@/types/tunnel';
import type { SyncRunDto, SyncRunDetailDto, SyncRunItemDto, TriggerSyncRequest } from '@/types/sync-run';
import type { PhoneListDto, PhoneListDetailDto, ContactDto } from '@/types/phone-list';
import type { FieldProfileDto, FieldProfileDetailDto, UpdateFieldProfileRequest } from '@/types/field-profile';
import type { SettingsDto, SettingsUpdateRequest } from '@/types/settings';
import type { DdgDto, DdgMemberDto } from '@/types/ddg';

const API_BASE = '/api';

async function fetchApi<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...options,
    credentials: 'include', // Always send httpOnly cookies
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
  });

  if (res.status === 401) {
    // Per D-09: redirect to /login on auth expiry, no toast
    if (typeof window !== 'undefined') {
      window.location.href = '/login';
    }
    throw new Error('Unauthorized');
  }

  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error(body.message || `API error: ${res.status}`);
  }

  // Handle 204 No Content
  if (res.status === 204) {
    return undefined as T;
  }

  return res.json();
}

export const api = {
  login: (username: string, password: string) =>
    fetchApi<{ message: string }>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username, password }),
    }),
  logout: () =>
    fetchApi<{ message: string }>('/auth/logout', { method: 'POST' }),
  me: () =>
    fetchApi<{ username: string }>('/auth/me'),

  dashboard: {
    get: () => fetchApi<DashboardDto>('/dashboard'),
  },

  tunnels: {
    list: () => fetchApi<TunnelDto[]>('/tunnels'),
    get: (id: number) => fetchApi<TunnelDetailDto>(`/tunnels/${id}`),
    update: (id: number, data: UpdateTunnelRequest) =>
      fetchApi<{ message: string }>(`/tunnels/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    updateStatus: (id: number, status: string) =>
      fetchApi<{ message: string }>(`/tunnels/${id}/status`, { method: 'PUT', body: JSON.stringify({ status }) }),
    delete: (id: number) =>
      fetchApi<void>(`/tunnels/${id}`, { method: 'DELETE' }),
  },

  syncRuns: {
    list: (page: number, pageSize: number) =>
      fetchApi<SyncRunDto[]>(`/sync-runs?page=${page}&pageSize=${pageSize}`),
    get: (id: number) => fetchApi<SyncRunDetailDto>(`/sync-runs/${id}`),
    getItems: (id: number, page: number, pageSize: number, action?: string) =>
      fetchApi<SyncRunItemDto[]>(
        `/sync-runs/${id}/items?page=${page}&pageSize=${pageSize}${action ? `&action=${action}` : ''}`
      ),
    trigger: (req: TriggerSyncRequest) =>
      fetchApi<{ runId: number }>('/sync-runs', { method: 'POST', body: JSON.stringify(req) }),
  },

  phoneLists: {
    list: () => fetchApi<PhoneListDto[]>('/phone-lists'),
    get: (id: number) => fetchApi<PhoneListDetailDto>(`/phone-lists/${id}`),
    getContacts: (id: number, page: number, pageSize: number) =>
      fetchApi<ContactDto[]>(`/phone-lists/${id}/contacts?page=${page}&pageSize=${pageSize}`),
  },

  fieldProfiles: {
    list: () => fetchApi<FieldProfileDto[]>('/field-profiles'),
    get: (id: number) => fetchApi<FieldProfileDetailDto>(`/field-profiles/${id}`),
    update: (id: number, data: UpdateFieldProfileRequest) =>
      fetchApi<{ message: string }>(`/field-profiles/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
  },

  settings: {
    list: () => fetchApi<SettingsDto[]>('/settings'),
    update: (data: SettingsUpdateRequest) =>
      fetchApi<{ message: string }>('/settings', { method: 'PUT', body: JSON.stringify(data) }),
  },

  ddgs: {
    list: () => fetchApi<DdgDto[]>('/graph/ddgs'),
    get: (id: string) => fetchApi<DdgDto>(`/graph/ddgs/${id}`),
    getMembers: (id: string, page: number, pageSize: number) =>
      fetchApi<DdgMemberDto[]>(`/graph/ddgs/${id}/members?page=${page}&pageSize=${pageSize}`),
  },
};
