export interface PhoneListDto {
  id: number;
  name: string;
  contactCount: number;
  userCount: number;
  sourceTunnels: PhoneListSourceTunnelDto[];
  lastSyncStatus: string | null;
}

export interface PhoneListDetailDto {
  id: number;
  name: string;
  description: string | null;
  exchangeFolderId: string | null;
  contactCount: number;
  userCount: number;
  sourceTunnels: PhoneListSourceTunnelDto[];
  createdAt: string;
  updatedAt: string;
}

export interface PhoneListSourceTunnelDto {
  id: number;
  name: string;
}

export interface ContactDto {
  sourceUserId: number;
  displayName: string | null;
  email: string | null;
  jobTitle: string | null;
  department: string | null;
  office: string | null;
  phone: string | null;
  mobilePhone: string | null;
  companyName: string | null;
  streetAddress: string | null;
  city: string | null;
  state: string | null;
  postalCode: string | null;
  country: string | null;
}
