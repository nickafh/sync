using AFHSync.Api.DTOs;
using AFHSync.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace AFHSync.Api.Controllers;

/// <summary>
/// DDG proxy endpoints for the frontend tunnel creation picker.
/// Per D-01: Exchange DDGs resolved via PowerShell, enriched with Graph data.
/// Per D-03: Called during tunnel setup only, not during sync runs.
/// </summary>
[ApiController]
[Route("api/graph")]
public class GraphController : ControllerBase
{
    private readonly IDDGResolver _ddgResolver;
    private readonly IFilterConverter _filterConverter;
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger<GraphController> _logger;

    public GraphController(
        IDDGResolver ddgResolver,
        IFilterConverter filterConverter,
        GraphServiceClient graphClient,
        ILogger<GraphController> logger)
    {
        _ddgResolver = ddgResolver;
        _filterConverter = filterConverter;
        _graphClient = graphClient;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/graph/ddgs - List all Dynamic Distribution Groups from Exchange Online.
    /// Returns DDGs enriched with filter conversion and member count from Graph.
    /// Per DDG-01: Lists all DDGs with displayName, recipientFilter, recipientFilterPlain, memberCount, type.
    /// </summary>
    [HttpGet("ddgs")]
    public async Task<ActionResult<DdgDto[]>> ListDdgs(CancellationToken ct)
    {
        try
        {
            var ddgs = await _ddgResolver.ListDdgsAsync(ct);
            var results = new List<DdgDto>();

            foreach (var ddg in ddgs)
            {
                var dto = await EnrichDdgAsync(ddg, ct);
                results.Add(dto);
            }

            return Ok(results.ToArray());
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("must be configured"))
        {
            _logger.LogWarning("Exchange Online not configured: {Message}", ex.Message);
            return StatusCode(503, new { message = "Exchange Online is not configured. Set Exchange:CertificatePath and Exchange:AppId in environment." });
        }
    }

    /// <summary>
    /// GET /api/graph/ddgs/{id} - Get a single DDG by identity.
    /// Returns DDG enriched with filter conversion and member count.
    /// Per DDG-02: Returns detailed DDG info with recipientFilter and conversion.
    /// </summary>
    [HttpGet("ddgs/{id}")]
    public async Task<ActionResult<DdgDto>> GetDdg(string id, CancellationToken ct)
    {
        try
        {
            var ddg = await _ddgResolver.GetDdgAsync(id, ct);
            if (ddg == null)
                return NotFound(new { message = $"DDG not found: {id}" });

            var dto = await EnrichDdgAsync(ddg, ct);
            return Ok(dto);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("must be configured"))
        {
            _logger.LogWarning("Exchange Online not configured: {Message}", ex.Message);
            return StatusCode(503, new { message = "Exchange Online is not configured. Set Exchange:CertificatePath and Exchange:AppId in environment." });
        }
    }

    /// <summary>
    /// GET /api/graph/ddgs/{id}/members?top=10 - Get sample members of a DDG via Graph.
    /// Uses the converted $filter to query users from Microsoft Graph.
    /// Per DDG-03: Returns sample members with displayName, email, jobTitle, department, officeLocation.
    /// </summary>
    [HttpGet("ddgs/{id}/members")]
    public async Task<ActionResult<DdgMemberDto[]>> GetDdgMembers(
        string id, [FromQuery] int top = 10, CancellationToken ct = default)
    {
        var ddg = await _ddgResolver.GetDdgAsync(id, ct);
        if (ddg == null)
            return NotFound(new { message = $"DDG not found: {id}" });

        var conversion = _filterConverter.Convert(ddg.RecipientFilter);
        if (!conversion.Success || string.IsNullOrWhiteSpace(conversion.Filter))
        {
            Response.Headers.Append("X-Filter-Warning",
                conversion.Warning ?? "Filter conversion failed");
            return Ok(Array.Empty<DdgMemberDto>());
        }

        try
        {
            var users = await _graphClient.Users.GetAsync(config =>
            {
                config.QueryParameters.Filter = conversion.Filter;
                config.QueryParameters.Top = top;
                config.QueryParameters.Select =
                    ["id", "displayName", "mail", "jobTitle", "department", "officeLocation"];
                config.Headers.Add("ConsistencyLevel", "eventual");
                config.QueryParameters.Count = true;
            }, ct);

            var members = (users?.Value ?? []).Select(u => new DdgMemberDto(
                Id: u.Id ?? string.Empty,
                DisplayName: u.DisplayName ?? string.Empty,
                Email: u.Mail,
                JobTitle: u.JobTitle,
                Department: u.Department,
                OfficeLocation: u.OfficeLocation
            )).ToArray();

            return Ok(members);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Graph member query failed for DDG {DdgId} with filter: {Filter}",
                id, conversion.Filter);
            Response.Headers.Append("X-Filter-Warning",
                $"Graph query failed: {ex.Message}");
            return Ok(Array.Empty<DdgMemberDto>());
        }
    }

    /// <summary>
    /// Enriches a DdgInfo from Exchange with filter conversion and Graph member count.
    /// </summary>
    private async Task<DdgDto> EnrichDdgAsync(DdgInfo ddg, CancellationToken ct)
    {
        var conversion = _filterConverter.Convert(ddg.RecipientFilter);
        var plainLanguage = _filterConverter.ToPlainLanguage(ddg.RecipientFilter);
        var type = DetermineType(ddg.RecipientFilter);

        var memberCount = 0;
        if (conversion.Success && !string.IsNullOrWhiteSpace(conversion.Filter))
        {
            memberCount = await GetMemberCountAsync(conversion.Filter, ddg.Id, ct);
        }

        return new DdgDto(
            Id: ddg.Id,
            DisplayName: ddg.DisplayName,
            PrimarySmtpAddress: ddg.PrimarySmtpAddress,
            RecipientFilter: ddg.RecipientFilter,
            RecipientFilterPlain: plainLanguage,
            GraphFilter: conversion.Filter,
            GraphFilterSuccess: conversion.Success,
            GraphFilterWarning: conversion.Warning,
            MemberCount: memberCount,
            Type: type
        );
    }

    /// <summary>
    /// Gets user count from Graph using the converted OData $filter.
    /// Returns 0 on failure (graceful degradation).
    /// </summary>
    private async Task<int> GetMemberCountAsync(string graphFilter, string ddgId, CancellationToken ct)
    {
        try
        {
            var users = await _graphClient.Users.GetAsync(config =>
            {
                config.QueryParameters.Filter = graphFilter;
                config.QueryParameters.Count = true;
                config.QueryParameters.Top = 1; // Only need the count, not the data
                config.QueryParameters.Select = ["id"];
                config.Headers.Add("ConsistencyLevel", "eventual");
            }, ct);

            return (int?)users?.OdataCount ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Graph member count query failed for DDG {DdgId} with filter: {Filter}",
                ddgId, graphFilter);
            return 0;
        }
    }

    /// <summary>
    /// Determines the DDG type based on its RecipientFilter content.
    /// </summary>
    private static string DetermineType(string recipientFilter)
    {
        if (string.IsNullOrWhiteSpace(recipientFilter))
            return "Other";

        if (recipientFilter.Contains("Office", StringComparison.OrdinalIgnoreCase))
            return "Office";
        if (recipientFilter.Contains("CustomAttribute2", StringComparison.OrdinalIgnoreCase))
            return "Brand";
        if (recipientFilter.Contains("Department", StringComparison.OrdinalIgnoreCase)
            || recipientFilter.Contains("Title", StringComparison.OrdinalIgnoreCase))
            return "Role";

        return "Other";
    }
}
