export interface TunnelSourceDto {
  id: number;
  sourceType: string;
  sourceIdentifier: string;
  sourceDisplayName: string | null;
  sourceSmtpAddress: string | null;
  sourceFilterPlain: string | null;
}

export interface SourceInput {
  sourceType: string;
  sourceIdentifier: string;
  sourceDisplayName: string | null;
  sourceSmtpAddress: string | null;
  sourceFilterPlain: string | null;
}

import type { TunnelStatus, StalePolicy, SyncRunStatus } from './common';

export interface TunnelDto {
  id: number;
  name: string;
  sources: TunnelSourceDto[];
  status: TunnelStatus;
  stalePolicy: StalePolicy;
  staleHoldDays: number;
  fieldProfileId: number | null;
  fieldProfileName: string | null;
  targetLists: TunnelTargetListDto[];
  estimatedContacts: number;
  estimatedTargetUsers: number;
  lastSync: TunnelLastSyncDto | null;
  photoSyncEnabled: boolean;
  targetGroupId: string | null;
  targetGroupName: string | null;
  targetUserEmails: string | null;
}

export interface TunnelDetailDto {
  id: number;
  name: string;
  sources: TunnelSourceDto[];
  status: TunnelStatus;
  stalePolicy: StalePolicy;
  staleHoldDays: number;
  fieldProfileId: number | null;
  fieldProfileName: string | null;
  targetLists: TunnelTargetListDto[];
  createdAt: string;
  updatedAt: string;
  photoSyncEnabled: boolean;
  targetGroupId: string | null;
  targetGroupName: string | null;
  targetUserEmails: string | null;
}

export interface TunnelTargetListDto {
  id: number;
  name: string;
}

export interface TunnelLastSyncDto {
  status: SyncRunStatus;
  completedAt: string | null;
  contactsUpdated: number;
}

export interface UpdateTunnelRequest {
  name: string;
  sources: SourceInput[];
  targetListIds: number[];
  fieldProfileId: number | null;
  stalePolicy: StalePolicy;
  staleDays: number;
  photoSyncEnabled?: boolean;
  targetGroupId?: string | null;
  targetGroupName?: string | null;
  targetUserEmails?: string | null;
}

export interface StatusUpdateRequest {
  status: TunnelStatus;
}

export interface CreateTunnelRequest {
  name: string;
  sources: SourceInput[];
  targetListIds: number[];
  fieldProfileId: number | null;
  stalePolicy: StalePolicy;
  staleDays: number;
  photoSyncEnabled?: boolean;
  targetGroupId?: string | null;
  targetGroupName?: string | null;
  targetUserEmails?: string | null;
}

export interface SecurityGroupDto {
  id: string;
  displayName: string;
  description: string | null;
  membershipRule: string | null;
}

export interface ImpactPreviewResponse {
  estimatedCreates: number;
  estimatedUpdates: number;
  estimatedRemovals: number;
}

export interface RefreshDdgResponse {
  message: string;
  graphFilter: string | null;
  filterPlain: string | null;
}

export interface OrgContactDto {
  id: string;
  displayName: string | null;
  email: string | null;
  companyName: string | null;
  department: string | null;
  jobTitle: string | null;
  businessPhone: string | null;
  isExcluded: boolean;
}

export interface OrgContactFilterInput {
  orgContactId: string;
  displayName: string | null;
  email: string | null;
  companyName: string | null;
  isExcluded: boolean;
}

export interface UserSearchResult {
  id: string;
  displayName: string;
  email: string;
  jobTitle: string | null;
}
