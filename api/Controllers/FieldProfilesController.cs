using AFHSync.Shared.Data;
using AFHSync.Api.DTOs;
using AFHSync.Shared.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/field-profiles")]
public class FieldProfilesController : ControllerBase
{
    private readonly AFHSyncDbContext _db;

    public FieldProfilesController(AFHSyncDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/field-profiles — List all field profiles with field counts.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var profiles = await _db.FieldProfiles
            .Include(p => p.FieldProfileFields)
            .AsNoTracking()
            .ToListAsync();

        var result = profiles.Select(p => new FieldProfileDto(
            p.Id,
            p.Name,
            p.Description,
            p.IsDefault,
            p.FieldProfileFields.Count
        )).ToArray();

        return Ok(result);
    }

    /// <summary>
    /// GET /api/field-profiles/{id} — Field profile detail with fields grouped by section.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var profile = await _db.FieldProfiles
            .Include(p => p.FieldProfileFields)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        if (profile is null)
            return NotFound(new { message = $"Field profile {id} not found." });

        // Group fields by FieldSection, ordered by DisplayOrder within each section
        var sections = profile.FieldProfileFields
            .GroupBy(f => f.FieldSection)
            .OrderBy(g => g.Key)
            .Select(g => new FieldSectionDto(
                g.Key,
                g.OrderBy(f => f.DisplayOrder)
                 .Select(f => new FieldSettingDto(
                     f.FieldName,
                     f.DisplayName,
                     EnumHelpers.ToPgName(f.Behavior)
                 ))
                 .ToArray()
            ))
            .ToArray();

        var dto = new FieldProfileDetailDto(
            profile.Id,
            profile.Name,
            profile.Description,
            profile.IsDefault,
            sections
        );

        return Ok(dto);
    }

    /// <summary>
    /// PUT /api/field-profiles/{id} — Update field behaviors for a profile.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateFieldProfileRequest request)
    {
        var profile = await _db.FieldProfiles
            .Include(p => p.FieldProfileFields)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (profile is null)
            return NotFound(new { message = $"Field profile {id} not found." });

        foreach (var entry in request.Fields)
        {
            var field = profile.FieldProfileFields
                .FirstOrDefault(f => f.FieldName == entry.FieldName);

            if (field is null)
                continue;

            // Parse behavior string to SyncBehavior enum (case-insensitive)
            if (Enum.TryParse<SyncBehavior>(entry.Behavior, ignoreCase: true, out var behavior))
            {
                field.Behavior = behavior;
            }
        }

        profile.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Field profile updated." });
    }
}
