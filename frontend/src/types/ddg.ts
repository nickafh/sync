export interface DdgDto {
  id: string;
  displayName: string;
  primarySmtpAddress: string;
  recipientFilter: string;
  recipientFilterPlain: string | null;
  graphFilter: string | null;
  graphFilterSuccess: boolean;
  graphFilterWarning: string | null;
  memberCount: number;
  type: string;
}

export interface DdgMemberDto {
  id: string;
  displayName: string;
  email: string | null;
  jobTitle: string | null;
  department: string | null;
  officeLocation: string | null;
}
