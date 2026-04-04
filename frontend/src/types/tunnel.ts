export interface TunnelDto {
  id: number;
  name: string;
  sourceType: string;
  sourceIdentifier: string;
  sourceDisplayName: string | null;
  sourceSmtpAddress: string | null;
  targetScope: string;
  status: string;
  stalePolicy: string;
  staleHoldDays: number;
  fieldProfileId: number | null;
  fieldProfileName: string | null;
  targetLists: TunnelTargetListDto[];
  estimatedContacts: number;
  estimatedTargetUsers: number;
  lastSync: TunnelLastSyncDto | null;
}

export interface TunnelDetailDto {
  id: number;
  name: string;
  sourceType: string;
  sourceIdentifier: string;
  sourceDisplayName: string | null;
  sourceSmtpAddress: string | null;
  targetScope: string;
  targetUserFilter: string | null;
  status: string;
  stalePolicy: string;
  staleHoldDays: number;
  fieldProfileId: number | null;
  fieldProfileName: string | null;
  targetLists: TunnelTargetListDto[];
  createdAt: string;
  updatedAt: string;
}

export interface TunnelTargetListDto {
  id: number;
  name: string;
}

export interface TunnelLastSyncDto {
  status: string;
  completedAt: string | null;
  contactsUpdated: number;
}

export interface UpdateTunnelRequest {
  name: string;
  sourceType: string;
  sourceIdentifier: string;
  sourceDisplayName: string | null;
  sourceSmtpAddress: string | null;
  targetScope: string;
  targetListIds: number[];
  fieldProfileId: number | null;
  stalePolicy: string;
  staleDays: number;
}

export interface StatusUpdateRequest {
  status: string;
}
