import type { DashboardDto } from '@/types/dashboard';
import type { TunnelDto, TunnelDetailDto, UpdateTunnelRequest, CreateTunnelRequest, ImpactPreviewResponse, RefreshDdgResponse } from '@/types/tunnel';
import type { SyncRunDto, SyncRunDetailDto, SyncRunItemDto, TriggerSyncRequest } from '@/types/sync-run';
import type { PhoneListDto, PhoneListDetailDto, ContactDto, CreatePhoneListRequest } from '@/types/phone-list';
import type { FieldProfileDto, FieldProfileDetailDto, UpdateFieldProfileRequest } from '@/types/field-profile';
import type { SettingsDto, SettingsUpdateRequest } from '@/types/settings';
import type { DdgDto, DdgMemberDto } from '@/types/ddg';
import type { SecurityGroupDto, OrgContactDto, OrgContactFilterInput, UserSearchResult, SourceContactDto, ContactExclusionInput } from '@/types/tunnel';

const API_BASE = '/api';

async function fetchApi<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...options,
    credentials: 'include', // Always send httpOnly cookies
    headers: {
      ...(options?.body ? { 'Content-Type': 'application/json' } : {}),
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
    create: (data: CreateTunnelRequest) =>
      fetchApi<{ id: number }>('/tunnels', { method: 'POST', body: JSON.stringify(data) }),
    preview: (id: number, data: UpdateTunnelRequest) =>
      fetchApi<ImpactPreviewResponse>(`/tunnels/${id}/preview`, { method: 'POST', body: JSON.stringify(data) }),
    refreshDdg: (id: number, sourceId: number) =>
      fetchApi<RefreshDdgResponse>(`/tunnels/${id}/sources/${sourceId}/refresh-ddg`, { method: 'POST' }),
    resetHashes: (id: number) =>
      fetchApi<{ count: number; message: string }>(`/tunnels/${id}/reset-hashes`, { method: 'POST' }),
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
    create: (data: CreatePhoneListRequest) =>
      fetchApi<{ id: number; name: string }>('/phone-lists', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: number, data: CreatePhoneListRequest) =>
      fetchApi<{ message: string }>(`/phone-lists/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: number) =>
      fetchApi<{ message: string }>(`/phone-lists/${id}`, { method: 'DELETE' }),
  },

  fieldProfiles: {
    list: () => fetchApi<FieldProfileDto[]>('/field-profiles'),
    get: (id: number) => fetchApi<FieldProfileDetailDto>(`/field-profiles/${id}`),
    update: (id: number, data: UpdateFieldProfileRequest) =>
      fetchApi<{ message: string }>(`/field-profiles/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
  },

  sync: {
    resetAllHashes: () =>
      fetchApi<{ count: number; message: string }>('/sync/reset-hashes', { method: 'POST' }),
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

  securityGroups: {
    list: () => fetchApi<SecurityGroupDto[]>('/graph/security-groups'),
  },

  users: {
    search: (q: string) => fetchApi<UserSearchResult[]>(`/graph/users/search?q=${encodeURIComponent(q)}`),
  },

  contactExclusions: {
    sourceContacts: (tunnelId: number) =>
      fetchApi<SourceContactDto[]>(`/tunnels/${tunnelId}/contact-exclusions/source-contacts`),
    resolve: (tunnelId: number) =>
      fetchApi<SourceContactDto[]>(`/tunnels/${tunnelId}/contact-exclusions/resolve`, {
        method: 'POST',
      }),
    getExclusions: (tunnelId: number) =>
      fetchApi<ContactExclusionInput[]>(`/tunnels/${tunnelId}/contact-exclusions`),
    updateExclusions: (tunnelId: number, exclusions: ContactExclusionInput[]) =>
      fetchApi<{ message: string }>(`/tunnels/${tunnelId}/contact-exclusions`, {
        method: 'PUT',
        body: JSON.stringify({ exclusions }),
      }),
  },

  orgContacts: {
    list: () => fetchApi<OrgContactDto[]>('/graph/org-contacts'),
    getFilters: (tunnelId: number) =>
      fetchApi<OrgContactFilterInput[]>(`/tunnels/${tunnelId}/org-contact-filters`),
    updateFilters: (tunnelId: number, filters: OrgContactFilterInput[]) =>
      fetchApi<{ message: string }>(`/tunnels/${tunnelId}/org-contact-filters`, {
        method: 'PUT',
        body: JSON.stringify({ filters }),
      }),
    bulkExclude: (tunnelId: number, companyName: string, isExcluded: boolean) =>
      fetchApi<{ count: number; message: string }>(`/tunnels/${tunnelId}/org-contact-filters/bulk-exclude`, {
        method: 'PATCH',
        body: JSON.stringify({ companyName, isExcluded }),
      }),
  },
};
