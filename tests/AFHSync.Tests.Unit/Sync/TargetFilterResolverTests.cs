using AFHSync.Api.DTOs;
using AFHSync.Api.Services;
using AFHSync.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for TargetFilterResolver — the static parser/union helper that backs
/// SyncEngine's PhoneList.TargetUserFilter handling (per quick-260417-2lb).
///
/// These cover the 7 behaviours enumerated in the task plan, focused purely on the
/// JSON parse + DDG-resolve + email-union pipeline. The Graph SDK call itself is
/// represented as a delegate (ddgMembersFromGraphFilter), so no Graph mock is needed.
/// </summary>
public class TargetFilterResolverTests
{
    // ---- helpers -------------------------------------------------------

    private static Func<string, CancellationToken, Task<List<string>>> StaticMembers(
        Dictionary<string, List<string>> filterToMembers)
    {
        return (filter, _) =>
            Task.FromResult(filterToMembers.TryGetValue(filter, out var list) ? list : new List<string>());
    }

    private sealed class FakeDdgResolver(Dictionary<string, DdgInfo?> map) : IDDGResolver
    {
        public Task<IReadOnlyList<DdgInfo>> ListDdgsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DdgInfo>>(map.Values.Where(v => v is not null).Cast<DdgInfo>().ToList());

        public Task<DdgInfo?> GetDdgAsync(string identity, CancellationToken ct = default)
            => Task.FromResult(map.TryGetValue(identity, out var info) ? info : null);
    }

    private sealed class FakeFilterConverter(Dictionary<string, FilterConversionResult> map) : IFilterConverter
    {
        public FilterConversionResult Convert(string opathFilter)
            => map.TryGetValue(opathFilter, out var r) ? r : new FilterConversionResult(false, "", "no mapping");

        public string ToPlainLanguage(string opathFilter) => opathFilter;
    }

    private static FakeDdgResolver Resolver(params (string id, DdgInfo? info)[] entries)
        => new(entries.ToDictionary(e => e.id, e => e.info));

    private static FakeFilterConverter Converter(params (string opath, string graphFilter)[] entries)
        => new(entries.ToDictionary(
            e => e.opath,
            e => new FilterConversionResult(true, e.graphFilter)));

    // ---- Test 1: emails only (back-compat) -----------------------------

    [Fact]
    public async Task ResolveAsync_EmailsOnly_ReturnsExplicitEmails()
    {
        var json = """{"emails":["a@x.com","b@x.com"]}""";
        var result = await TargetFilterResolver.ResolveAsync(
            json,
            Resolver(),
            Converter(),
            StaticMembers(new()),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(2, result.Emails.Count);
        Assert.Contains("a@x.com", result.Emails);
        Assert.Contains("b@x.com", result.Emails);
    }

    // ---- Test 2: null/empty JSON ---------------------------------------

    [Fact]
    public async Task ResolveAsync_NullJson_ReturnsEmptySet()
    {
        var result = await TargetFilterResolver.ResolveAsync(
            null,
            Resolver(),
            Converter(),
            StaticMembers(new()),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Empty(result.Emails);
    }

    [Fact]
    public async Task ResolveAsync_EmptyObjectJson_ReturnsEmptySet()
    {
        var result = await TargetFilterResolver.ResolveAsync(
            "{}",
            Resolver(),
            Converter(),
            StaticMembers(new()),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Empty(result.Emails);
    }

    // ---- Test 3: ddgs only ---------------------------------------------

    [Fact]
    public async Task ResolveAsync_DdgsOnly_ReturnsResolvedMembers()
    {
        var json = """{"ddgs":[{"id":"g1","displayName":"X"}]}""";

        var ddg = new DdgInfo("g1", "X", "x@afh.com", "RecipientFilter X");
        var resolver = Resolver(("g1", ddg));
        var converter = Converter(("RecipientFilter X", "officeLocation eq 'X'"));
        var members = StaticMembers(new()
        {
            ["officeLocation eq 'X'"] = new List<string> { "b@x.com", "c@x.com" }
        });

        var result = await TargetFilterResolver.ResolveAsync(
            json, resolver, converter, members, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, result.Emails.Count);
        Assert.Contains("b@x.com", result.Emails);
        Assert.Contains("c@x.com", result.Emails);
    }

    // ---- Test 4: union + case-insensitive dedupe -----------------------

    [Fact]
    public async Task ResolveAsync_EmailsPlusDdgs_DedupedCaseInsensitive()
    {
        var json = """{"emails":["A@X.com"],"ddgs":[{"id":"g1","displayName":"X"}]}""";

        var ddg = new DdgInfo("g1", "X", "x@afh.com", "Filter1");
        var resolver = Resolver(("g1", ddg));
        var converter = Converter(("Filter1", "graphF1"));
        var members = StaticMembers(new()
        {
            // a@x.com — same as A@X.com modulo case → must dedupe
            ["graphF1"] = new List<string> { "a@x.com", "b@x.com" }
        });

        var result = await TargetFilterResolver.ResolveAsync(
            json, resolver, converter, members, NullLogger.Instance, CancellationToken.None);

        // Two distinct addresses (a@x.com and b@x.com); A@X.com folded into a@x.com.
        Assert.Equal(2, result.Emails.Count);
        Assert.Contains("a@x.com", result.Emails, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("b@x.com", result.Emails, StringComparer.OrdinalIgnoreCase);
    }

    // ---- Test 5: missing DDG (resolver returns null) -------------------

    [Fact]
    public async Task ResolveAsync_DdgNotFound_LogsWarningAndContinuesWithOtherDdgs()
    {
        var json = """{"emails":["x@afh.com"],"ddgs":[{"id":"missing","displayName":"Gone"},{"id":"g1","displayName":"X"}]}""";

        var ddg = new DdgInfo("g1", "X", "x@afh.com", "Filter1");
        // "missing" maps to null → not found
        var resolver = new FakeDdgResolver(new Dictionary<string, DdgInfo?>
        {
            ["missing"] = null,
            ["g1"] = ddg,
        });
        var converter = Converter(("Filter1", "graphF1"));
        var members = StaticMembers(new()
        {
            ["graphF1"] = new List<string> { "b@x.com" }
        });

        var result = await TargetFilterResolver.ResolveAsync(
            json, resolver, converter, members, NullLogger.Instance, CancellationToken.None);

        // Explicit email + the surviving DDG's one member.
        Assert.Equal(2, result.Emails.Count);
        Assert.Contains("x@afh.com", result.Emails);
        Assert.Contains("b@x.com", result.Emails);
    }

    // ---- Test 6: empty DDG (resolver succeeds, Graph yields zero) ------

    [Fact]
    public async Task ResolveAsync_DdgZeroMembers_LogsWarningAndContinues()
    {
        var json = """{"emails":["x@afh.com"],"ddgs":[{"id":"g1","displayName":"Empty"}]}""";

        var ddg = new DdgInfo("g1", "Empty", "empty@afh.com", "Filter1");
        var resolver = Resolver(("g1", ddg));
        var converter = Converter(("Filter1", "graphF1"));
        // Empty list of members
        var members = StaticMembers(new()
        {
            ["graphF1"] = new List<string>()
        });

        var result = await TargetFilterResolver.ResolveAsync(
            json, resolver, converter, members, NullLogger.Instance, CancellationToken.None);

        // Only the explicit email survives.
        Assert.Single(result.Emails);
        Assert.Contains("x@afh.com", result.Emails);
    }

    // ---- Test 7: missing ddgs key (back-compat with pre-2lb shape) -----

    [Fact]
    public async Task ResolveAsync_MissingDdgsKey_BackwardsCompatible()
    {
        // Existing rows in the wild — emails only, no ddgs key at all.
        var json = """{"emails":["legacy@afh.com"]}""";

        var result = await TargetFilterResolver.ResolveAsync(
            json,
            // Resolver should never even be called.
            new FakeDdgResolver(new Dictionary<string, DdgInfo?>()),
            Converter(),
            StaticMembers(new()),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Single(result.Emails);
        Assert.Contains("legacy@afh.com", result.Emails);
    }

    // ---- Bonus: filter-converter rejection path ------------------------

    [Fact]
    public async Task ResolveAsync_FilterConverterFails_SkipsDdgAndContinues()
    {
        var json = """{"emails":["x@afh.com"],"ddgs":[{"id":"g1","displayName":"Bad"}]}""";

        var ddg = new DdgInfo("g1", "Bad", "bad@afh.com", "Unsupported OPATH");
        var resolver = Resolver(("g1", ddg));
        // Converter has no mapping → returns Success=false
        var converter = new FakeFilterConverter(new Dictionary<string, FilterConversionResult>());
        var members = StaticMembers(new());

        var result = await TargetFilterResolver.ResolveAsync(
            json, resolver, converter, members, NullLogger.Instance, CancellationToken.None);

        Assert.Single(result.Emails);
        Assert.Contains("x@afh.com", result.Emails);
    }

    // ---- Bonus: malformed JSON does not throw --------------------------

    [Fact]
    public async Task ResolveAsync_MalformedJson_ReturnsEmptySet()
    {
        var result = await TargetFilterResolver.ResolveAsync(
            "{not valid json",
            Resolver(),
            Converter(),
            StaticMembers(new()),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Empty(result.Emails);
    }

    // ---- Failure reporting (Phase 1: DDG failures must be visible) ------------------------

    [Fact]
    public async Task ResolveAsync_DdgNotFound_ReportsFailureAndKeepsEmails()
    {
        var json = """{"emails":["a@x.com"],"ddgs":[{"id":"ddg-1","displayName":"Buckhead Staff"}]}""";

        var result = await TargetFilterResolver.ResolveAsync(
            json, Resolver(("ddg-1", null)), Converter(), StaticMembers(new()), NullLogger.Instance, CancellationToken.None);

        Assert.Single(result.Emails);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("ddg-1", failure.Id);
        Assert.Equal("Buckhead Staff", failure.DisplayName);
        Assert.Contains("not found", failure.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_ConversionFails_ReportsFailureWithConverterWarning()
    {
        var json = """{"ddgs":[{"id":"ddg-1","displayName":"Buckhead Staff"}]}""";
        var resolver = Resolver(("ddg-1", new DdgInfo("ddg-1", "Buckhead Staff", "bs@x.com", "(RecipientTypeDetails -eq 'MailContact')")));
        var converter = new FakeFilterConverter(new()
        {
            ["(RecipientTypeDetails -eq 'MailContact')"] = new FilterConversionResult(false, "", "Filter matches no users")
        });

        var result = await TargetFilterResolver.ResolveAsync(
            json, resolver, converter, StaticMembers(new()), NullLogger.Instance, CancellationToken.None);

        Assert.Empty(result.Emails);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("Filter matches no users", failure.Reason);
    }

    [Fact]
    public async Task ResolveAsync_GraphQueryThrows_ReportsFailure()
    {
        var json = """{"ddgs":[{"id":"ddg-1","displayName":"Buckhead Staff"}]}""";
        var resolver = Resolver(("ddg-1", new DdgInfo("ddg-1", "Buckhead Staff", "bs@x.com", "(Office -eq 'Buckhead')")));
        var converter = Converter(("(Office -eq 'Buckhead')", "officeLocation eq 'Buckhead'"));
        Func<string, CancellationToken, Task<List<string>>> throwing =
            (_, _) => throw new InvalidOperationException("Request_UnsupportedQuery");

        var result = await TargetFilterResolver.ResolveAsync(
            json, resolver, converter, throwing, NullLogger.Instance, CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Contains("Request_UnsupportedQuery", failure.Reason);
    }

    [Fact]
    public async Task ResolveAsync_ZeroMembers_ReportsFailure()
    {
        var json = """{"ddgs":[{"id":"ddg-1","displayName":"Buckhead Staff"}]}""";
        var resolver = Resolver(("ddg-1", new DdgInfo("ddg-1", "Buckhead Staff", "bs@x.com", "(Office -eq 'Buckhead')")));
        var converter = Converter(("(Office -eq 'Buckhead')", "officeLocation eq 'Buckhead'"));

        var result = await TargetFilterResolver.ResolveAsync(
            json, resolver, converter, StaticMembers(new()), NullLogger.Instance, CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Contains("0 members", failure.Reason);
    }

    [Fact]
    public async Task ResolveAsync_HealthyDdg_HasNoFailures()
    {
        var json = """{"ddgs":[{"id":"ddg-1","displayName":"Buckhead Staff"}]}""";
        var resolver = Resolver(("ddg-1", new DdgInfo("ddg-1", "Buckhead Staff", "bs@x.com", "(Office -eq 'Buckhead')")));
        var converter = Converter(("(Office -eq 'Buckhead')", "officeLocation eq 'Buckhead'"));
        var members = StaticMembers(new() { ["officeLocation eq 'Buckhead'"] = ["u1@x.com", "u2@x.com"] });

        var result = await TargetFilterResolver.ResolveAsync(
            json, resolver, converter, members, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, result.Emails.Count);
        Assert.Empty(result.Failures);
    }
}
