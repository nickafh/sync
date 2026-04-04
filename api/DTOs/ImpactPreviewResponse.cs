namespace AFHSync.Api.DTOs;

public record ImpactPreviewResponse(
    int EstimatedCreates,
    int EstimatedUpdates,
    int EstimatedRemovals
);
