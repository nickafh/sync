namespace AFHSync.Api.DTOs;

public record UpdateFieldProfileRequest(FieldUpdateEntry[] Fields);
public record FieldUpdateEntry(string FieldName, string Behavior);
