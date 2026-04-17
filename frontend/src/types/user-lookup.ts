export type UserFolderDto = {
  folderId: string;
  folderName: string;
  graphContactCount: number;
  matchedTunnelId: number | null;
  matchedTunnelName: string | null;
  expectedContactCount: number | null;
  lastSyncedAt: string | null;
};

export type UserOrphanTunnelDto = {
  tunnelId: number;
  tunnelName: string;
  expectedContactCount: number;
  lastSyncedAt: string | null;
};

export type UserFolderStateDto = {
  email: string;
  entraId: string | null;
  displayName: string | null;
  isTrackedTargetMailbox: boolean;
  targetMailboxId: number | null;
  folders: UserFolderDto[];
  orphanTunnels: UserOrphanTunnelDto[];
};
