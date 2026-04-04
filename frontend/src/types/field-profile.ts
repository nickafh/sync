export interface FieldProfileDto {
  id: number;
  name: string;
  description: string | null;
  isDefault: boolean;
  fieldCount: number;
}

export interface FieldProfileDetailDto {
  id: number;
  name: string;
  description: string | null;
  isDefault: boolean;
  sections: FieldSectionDto[];
}

export interface FieldSectionDto {
  name: string;
  fields: FieldSettingDto[];
}

export interface FieldSettingDto {
  fieldName: string;
  displayName: string;
  behavior: string;
}

export interface UpdateFieldProfileRequest {
  fields: FieldUpdateEntry[];
}

export interface FieldUpdateEntry {
  fieldName: string;
  behavior: string;
}
