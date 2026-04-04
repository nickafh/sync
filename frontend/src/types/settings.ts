export interface SettingsDto {
  key: string;
  value: string;
  description: string | null;
}

export interface SettingsUpdateRequest {
  settings: SettingEntry[];
}

export interface SettingEntry {
  key: string;
  value: string;
}
