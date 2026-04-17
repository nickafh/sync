export interface PhoneListDto {
  id: number;
  name: string;
  contactCount: number;
  userCount: number;
  targetScope: string;
  targetUserFilter: string | null;
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
  targetScope: string;
  targetUserFilter: string | null;
  sourceTunnels: PhoneListSourceTunnelDto[];
  createdAt: string;
  updatedAt: string;
}

export interface PhoneListSourceTunnelDto {
  id: number;
  name: string;
}

export interface CreatePhoneListRequest {
  name: string;
  description: string | null;
  targetScope?: string;
  targetUserFilter?: string | null;
}

/**
 * quick-260417-2lb: PhoneList.targetUserFilter is a JSON string on the wire (back-compat
 * with the API contract — keep `targetUserFilter: string | null` on PhoneListDto). When
 * parsed, it conforms to TargetUserFilterShape: any combination of explicit emails plus
 * a list of Exchange Dynamic Distribution Groups whose members are unioned in at sync time.
 *
 * Both keys are optional. Old rows containing only `{"emails":[...]}` continue to deserialize
 * cleanly because `ddgs` simply defaults to undefined → empty.
 */
export interface TargetUserFilterDdg {
  id: string;
  displayName: string;
}

export interface TargetUserFilterShape {
  emails?: string[];
  ddgs?: TargetUserFilterDdg[];
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
