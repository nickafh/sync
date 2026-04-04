namespace AFHSync.Api.DTOs;

public record SettingsUpdateRequest(SettingEntry[] Settings);
public record SettingEntry(string Key, string Value);
