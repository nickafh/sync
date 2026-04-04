export interface DashboardDto {
  activeTunnels: number;
  totalPhoneLists: number;
  totalTargetUsers: number;
  lastSync: DashboardLastSyncDto | null;
  warnings: DashboardWarningDto[];
  recentRuns: DashboardRecentRunDto[];
}

export interface DashboardLastSyncDto {
  id: number;
  status: string;
  startedAt: string | null;
  durationMs: number | null;
  contactsCreated: number;
  contactsUpdated: number;
  contactsRemoved: number;
  photosUpdated: number;
}

export interface DashboardWarningDto {
  type: string;
  tunnelId: number | null;
  message: string;
}

export interface DashboardRecentRunDto {
  id: number;
  runType: string;
  status: string;
  startedAt: string | null;
  durationMs: number | null;
  contactsUpdated: number;
}
