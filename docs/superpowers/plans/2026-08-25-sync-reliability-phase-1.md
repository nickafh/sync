# Sync Reliability — Phase 1 Implementation Plan (DDG filter conversion + failure visibility)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every Exchange DDG in the tenant convert to a valid Graph `$filter` again (restoring the ~349 missing "Avalon Users" targets), and make any future DDG resolution failure loud instead of silent.

**Architecture:** Replace the regex-based `FilterConverter` with a small OPATH tokenizer + recursive-descent parser that produces an AST; Exchange-only predicates (`RecipientTypeDetails`, `HiddenFromAddressListsEnabled`, `Name -like`, `RecipientTypeDetailsValue`) are folded to boolean constants and simplified away; the remaining tree is rendered to OData. Unknown attributes now make conversion **fail** (`Success=false`). Consumers (refresh endpoint, worker target resolution, UI pickers) stop treating a warning as success, and the worker records each DDG failure as a run item so it shows in Runs & Logs.

**Tech Stack:** .NET 10 / ASP.NET Core (api), .NET 10 worker with Hangfire, EF Core (Postgres in prod, InMemory in tests), xUnit 2.9; Next.js 15 / React / TypeScript frontend with TanStack Query, sonner toasts, cmdk `Command` list.

**Spec:** `docs/superpowers/specs/2026-08-25-sync-reliability-design.md` (Phase 1 section). Real DDG filters: `docs/superpowers/specs/2026-08-25-ddg-recipient-filters.json`.

## Global Constraints

- Branch: `sync-reliability/phase-1` (already created; spec is committed on it). PR target: `main` on `github.com/nickafh/sync`.
- `Success=false` whenever any unmapped attribute survives simplification, the parser fails, or the whole filter folds to a constant.
- Quoted values must never be rewritten (attribute renaming is done on AST nodes only).
- Frontend gate is `npm run build` (there is no component test harness); backend gate is `dotnet test` for `tests/AFHSync.Tests.Unit`.
- Commit after every task. Use `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` as the last line of each commit message.
- Run all shell commands from the repo root `/Users/nick/Documents/Code/AFHsync` unless stated.

---

## File map

| File | Responsibility |
|---|---|
| `api/Services/Opath/OpathAst.cs` (new) | AST records: `OpathNode`, `OpathConst`, `OpathCompare`, `OpathNot`, `OpathAnd`, `OpathOr`; `OpathParseException` |
| `api/Services/Opath/OpathTokenizer.cs` (new) | `OpathTokenizer.Tokenize(string) → List<OpathToken>` |
| `api/Services/Opath/OpathParser.cs` (new) | `OpathParser.Parse(string) → OpathNode` |
| `api/Services/Opath/OpathFolder.cs` (new) | `Fold` (Exchange-only predicates → constants, collect unknown attrs) and `Simplify` |
| `api/Services/Opath/ODataRenderer.cs` (new) | AST → OData string |
| `api/Services/Opath/PlainLanguageRenderer.cs` (new) | AST → human-readable string |
| `api/Services/FilterConverter.cs` (rewrite) | Orchestrates parse → fold → simplify → render; `AttributeMap`/`PlainNameMap` become `internal static` |
| `api/DTOs/FilterConversionResult.cs` | adds `UnknownAttributes` |
| `api/Controllers/TunnelsController.cs:577-585` | `RefreshDdg` returns 422 on failed conversion |
| `worker/Services/TargetFilterResolver.cs` | returns `TargetFilterResolution` (emails + failures) |
| `worker/Services/SyncEngine.cs:364-447` | records DDG failures; empty SpecificUsers scope ⇒ zero mailboxes |
| `worker/Services/SourceResolver.cs:360-364` | Notes from `extensionAttribute5` only |
| `frontend/src/components/DDGSearchList.tsx` | unconvertible DDGs disabled with reason |
| `frontend/src/components/wizard/StepSource.tsx:42-50` | guard on select |
| `frontend/src/components/TunnelWizard.tsx:146-156` | no raw-OPATH fallback |
| `frontend/src/app/(app)/tunnels/[id]/page.tsx:251-262, 574` | guard on select; hide refresh button when no SMTP |
| `frontend/src/app/(app)/lists/page.tsx:463-496, 600-620` | Groups tab disables unconvertible DDGs |
| `frontend/src/components/DDGRefreshButton.tsx:30-36` | toast shows real message |
| `tests/AFHSync.Tests.Unit/Api/OpathParserTests.cs` (new), `FilterConverterTests.cs` (update), `Fixtures/ddg-recipient-filters.json` (new), `tests/AFHSync.Tests.Unit/Sync/TargetFilterResolverTests.cs` (update), `SyncEngineTests.cs` (add test) | tests |

---

### Task 0: Baseline

**Files:** none

- [ ] **Step 1: Confirm branch and clean tree**

Run: `git status --short && git branch --show-current`
Expected: no output from status; branch `sync-reliability/phase-1`.

- [ ] **Step 2: Build and run the existing unit tests to establish the baseline**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -5`
Expected: `Passed!` with 154 tests (or the count reported). If the integration project fails to build for environment reasons (it is referenced by the unit project), note the error before continuing — everything below only needs the unit project.

- [ ] **Step 3: Confirm the frontend builds**

Run: `cd frontend && (test -d node_modules || npm install) && npm run build 2>&1 | tail -5; cd ..`
Expected: `✓ Compiled successfully` (or Next's equivalent) with no type errors.

---

### Task 1: OPATH AST, tokenizer and parser

**Files:**
- Create: `api/Services/Opath/OpathAst.cs`
- Create: `api/Services/Opath/OpathTokenizer.cs`
- Create: `api/Services/Opath/OpathParser.cs`
- Test: `tests/AFHSync.Tests.Unit/Api/OpathParserTests.cs`

**Interfaces:**
- Produces: `OpathNode` hierarchy; `OpathParser.Parse(string) : OpathNode` (throws `OpathParseException(string message, int position)`); `OpathTokenizer.Tokenize(string) : List<OpathToken>`.
- `OpathCompare.Operator` is one of the lowercase strings `eq ne like notlike gt lt ge le`; `OpathCompare.Value` is the unescaped literal (a `'It''s'` literal yields `It's`; `$false` yields `false`).

- [ ] **Step 1: Write the failing parser tests**

Create `tests/AFHSync.Tests.Unit/Api/OpathParserTests.cs`:

```csharp
using AFHSync.Api.Services.Opath;

namespace AFHSync.Tests.Unit.Api;

public class OpathParserTests
{
    [Fact]
    public void Parse_SimpleComparison_ReturnsCompareNode()
    {
        var node = OpathParser.Parse("(Office -eq 'Buckhead')");

        var cmp = Assert.IsType<OpathCompare>(node);
        Assert.Equal("Office", cmp.Attribute);
        Assert.Equal("eq", cmp.Operator);
        Assert.Equal("Buckhead", cmp.Value);
    }

    [Fact]
    public void Parse_AndBindsTighterThanOr()
    {
        // a -or b -and c  ==>  Or(a, And(b, c))
        var node = OpathParser.Parse("Office -eq 'A' -or Office -eq 'B' -and Department -eq 'C'");

        var or = Assert.IsType<OpathOr>(node);
        Assert.IsType<OpathCompare>(or.Left);
        var and = Assert.IsType<OpathAnd>(or.Right);
        Assert.Equal("B", ((OpathCompare)and.Left).Value);
        Assert.Equal("C", ((OpathCompare)and.Right).Value);
    }

    [Fact]
    public void Parse_ParenthesesOverridePrecedence()
    {
        var node = OpathParser.Parse("(Office -eq 'A' -or Office -eq 'B') -and Department -eq 'C'");

        var and = Assert.IsType<OpathAnd>(node);
        Assert.IsType<OpathOr>(and.Left);
        Assert.IsType<OpathCompare>(and.Right);
    }

    [Fact]
    public void Parse_NotWithParenthesizedInner()
    {
        var node = OpathParser.Parse("-not(Name -like 'SystemMailbox{*')");

        var not = Assert.IsType<OpathNot>(node);
        var cmp = Assert.IsType<OpathCompare>(not.Inner);
        Assert.Equal("Name", cmp.Attribute);
        Assert.Equal("like", cmp.Operator);
        Assert.Equal("SystemMailbox{*", cmp.Value);
    }

    [Fact]
    public void Parse_EscapedQuoteInsideLiteral()
    {
        var node = OpathParser.Parse("(Company -eq 'Sotheby''s')");

        Assert.Equal("Sotheby's", ((OpathCompare)node).Value);
    }

    [Fact]
    public void Parse_DollarBooleanValue()
    {
        var node = OpathParser.Parse("(HiddenFromAddressListsEnabled -eq $false)");

        var cmp = Assert.IsType<OpathCompare>(node);
        Assert.Equal("false", cmp.Value);
    }

    [Fact]
    public void Parse_OperatorsAreCaseInsensitive()
    {
        var node = OpathParser.Parse("(office -EQ 'Buckhead') -AND (Department -Ne 'X')");

        var and = Assert.IsType<OpathAnd>(node);
        Assert.Equal("eq", ((OpathCompare)and.Left).Operator);
        Assert.Equal("ne", ((OpathCompare)and.Right).Operator);
    }

    [Fact]
    public void Parse_DeeplyNestedRedundantParens()
    {
        var node = OpathParser.Parse("((((((Office -eq 'Buckhead'))))))");

        Assert.IsType<OpathCompare>(node);
    }

    [Fact]
    public void Parse_UnterminatedString_Throws()
    {
        var ex = Assert.Throws<OpathParseException>(() => OpathParser.Parse("(Office -eq 'Buckhead)"));
        Assert.Contains("Unterminated", ex.Message);
    }

    [Fact]
    public void Parse_UnknownOperator_Throws()
    {
        Assert.Throws<OpathParseException>(() => OpathParser.Parse("(Office -contains 'Buckhead')"));
    }

    [Fact]
    public void Parse_TrailingGarbage_Throws()
    {
        Assert.Throws<OpathParseException>(() => OpathParser.Parse("(Office -eq 'Buckhead')) extra"));
    }
}
```

- [ ] **Step 2: Run to verify the tests fail to compile**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~OpathParserTests" 2>&1 | tail -3`
Expected: build error `The type or namespace name 'Opath' does not exist`.

- [ ] **Step 3: Create the AST**

Create `api/Services/Opath/OpathAst.cs`:

```csharp
namespace AFHSync.Api.Services.Opath;

/// <summary>Base of the OPATH filter AST produced by <see cref="OpathParser"/>.</summary>
public abstract record OpathNode;

/// <summary>A boolean constant. Produced by folding Exchange-only predicates.</summary>
public sealed record OpathConst(bool Value) : OpathNode;

/// <summary>
/// A comparison such as <c>Office -eq 'Buckhead'</c>.
/// <paramref name="Operator"/> is lowercase without the dash: eq, ne, like, notlike, gt, lt, ge, le.
/// <paramref name="Value"/> is the unescaped literal.
/// </summary>
public sealed record OpathCompare(string Attribute, string Operator, string Value) : OpathNode;

public sealed record OpathNot(OpathNode Inner) : OpathNode;

public sealed record OpathAnd(OpathNode Left, OpathNode Right) : OpathNode;

public sealed record OpathOr(OpathNode Left, OpathNode Right) : OpathNode;

public sealed class OpathParseException(string message, int position)
    : Exception($"{message} (at position {position})")
{
    public int Position { get; } = position;
}
```

- [ ] **Step 4: Create the tokenizer**

Create `api/Services/Opath/OpathTokenizer.cs`:

```csharp
using System.Text;

namespace AFHSync.Api.Services.Opath;

public enum OpathTokenKind { LParen, RParen, Identifier, Operator, And, Or, Not, String, Boolean, Number, End }

public readonly record struct OpathToken(OpathTokenKind Kind, string Text, int Position);

public static class OpathTokenizer
{
    private static readonly HashSet<string> ComparisonOperators =
        ["eq", "ne", "like", "notlike", "gt", "lt", "ge", "le"];

    public static List<OpathToken> Tokenize(string input)
    {
        var tokens = new List<OpathToken>();
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { tokens.Add(new(OpathTokenKind.LParen, "(", i)); i++; continue; }
            if (c == ')') { tokens.Add(new(OpathTokenKind.RParen, ")", i)); i++; continue; }

            if (c == '\'' || c == '"')
            {
                tokens.Add(ReadString(input, ref i, c));
                continue;
            }

            if (c == '-')
            {
                int start = i;
                i++;
                int wordStart = i;
                while (i < input.Length && char.IsLetter(input[i])) i++;
                var word = input[wordStart..i].ToLowerInvariant();
                tokens.Add(word switch
                {
                    "and" => new OpathToken(OpathTokenKind.And, "-and", start),
                    "or" => new OpathToken(OpathTokenKind.Or, "-or", start),
                    "not" => new OpathToken(OpathTokenKind.Not, "-not", start),
                    _ when ComparisonOperators.Contains(word) => new OpathToken(OpathTokenKind.Operator, word, start),
                    _ => throw new OpathParseException($"Unknown operator '-{word}'", start)
                });
                continue;
            }

            if (c == '$')
            {
                int start = i;
                i++;
                int wordStart = i;
                while (i < input.Length && char.IsLetter(input[i])) i++;
                var word = input[wordStart..i].ToLowerInvariant();
                if (word is not ("true" or "false"))
                    throw new OpathParseException($"Unknown variable '${word}'", start);
                tokens.Add(new(OpathTokenKind.Boolean, word, start));
                continue;
            }

            if (char.IsDigit(c))
            {
                int start = i;
                while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.')) i++;
                tokens.Add(new(OpathTokenKind.Number, input[start..i], start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_')) i++;
                tokens.Add(new(OpathTokenKind.Identifier, input[start..i], start));
                continue;
            }

            throw new OpathParseException($"Unexpected character '{c}'", i);
        }

        tokens.Add(new(OpathTokenKind.End, string.Empty, input.Length));
        return tokens;
    }

    /// <summary>Reads a quoted literal. A doubled quote inside the literal is an escaped quote.</summary>
    private static OpathToken ReadString(string input, ref int i, char quote)
    {
        int start = i;
        i++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= input.Length)
                throw new OpathParseException("Unterminated string literal", start);

            if (input[i] == quote)
            {
                if (i + 1 < input.Length && input[i + 1] == quote)
                {
                    sb.Append(quote);
                    i += 2;
                    continue;
                }
                i++; // closing quote
                break;
            }

            sb.Append(input[i]);
            i++;
        }
        return new OpathToken(OpathTokenKind.String, sb.ToString(), start);
    }
}
```

- [ ] **Step 5: Create the parser**

Create `api/Services/Opath/OpathParser.cs`:

```csharp
namespace AFHSync.Api.Services.Opath;

/// <summary>
/// Recursive-descent parser for Exchange OPATH recipient filters.
/// Grammar (precedence low → high):  or := and ('-or' and)*  |  and := not ('-and' not)*  |
/// not := '-not' not | primary  |  primary := '(' or ')' | $bool | Identifier Operator Value
/// </summary>
public sealed class OpathParser
{
    private readonly List<OpathToken> _tokens;
    private int _pos;

    private OpathParser(List<OpathToken> tokens) => _tokens = tokens;

    public static OpathNode Parse(string input)
    {
        var parser = new OpathParser(OpathTokenizer.Tokenize(input));
        var node = parser.ParseOr();
        parser.Expect(OpathTokenKind.End);
        return node;
    }

    private OpathToken Peek => _tokens[_pos];

    private OpathToken Advance() => _tokens[_pos++];

    private OpathToken Expect(OpathTokenKind kind)
    {
        var token = Peek;
        if (token.Kind != kind)
            throw new OpathParseException(
                $"Expected {kind} but found '{(token.Kind == OpathTokenKind.End ? "end of filter" : token.Text)}'",
                token.Position);
        return Advance();
    }

    private OpathNode ParseOr()
    {
        var left = ParseAnd();
        while (Peek.Kind == OpathTokenKind.Or)
        {
            Advance();
            left = new OpathOr(left, ParseAnd());
        }
        return left;
    }

    private OpathNode ParseAnd()
    {
        var left = ParseNot();
        while (Peek.Kind == OpathTokenKind.And)
        {
            Advance();
            left = new OpathAnd(left, ParseNot());
        }
        return left;
    }

    private OpathNode ParseNot()
    {
        if (Peek.Kind == OpathTokenKind.Not)
        {
            Advance();
            return new OpathNot(ParseNot());
        }
        return ParsePrimary();
    }

    private OpathNode ParsePrimary()
    {
        var token = Peek;
        switch (token.Kind)
        {
            case OpathTokenKind.LParen:
            {
                Advance();
                var inner = ParseOr();
                Expect(OpathTokenKind.RParen);
                return inner;
            }
            case OpathTokenKind.Boolean:
                Advance();
                return new OpathConst(token.Text == "true");
            case OpathTokenKind.Identifier:
            {
                var attr = Advance();
                var op = Expect(OpathTokenKind.Operator);
                var value = Peek;
                if (value.Kind is OpathTokenKind.String or OpathTokenKind.Boolean
                    or OpathTokenKind.Number or OpathTokenKind.Identifier)
                {
                    Advance();
                    return new OpathCompare(attr.Text, op.Text, value.Text);
                }
                throw new OpathParseException($"Expected a value after '{attr.Text} -{op.Text}'", value.Position);
            }
            default:
                throw new OpathParseException(
                    $"Unexpected '{(token.Kind == OpathTokenKind.End ? "end of filter" : token.Text)}'",
                    token.Position);
        }
    }
}
```

- [ ] **Step 6: Run the parser tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~OpathParserTests" 2>&1 | tail -3`
Expected: `Passed! - Failed: 0, Passed: 11`.

- [ ] **Step 7: Commit**

```bash
git add api/Services/Opath tests/AFHSync.Tests.Unit/Api/OpathParserTests.cs
git commit -m "feat(opath): tokenizer + recursive-descent parser for Exchange recipient filters

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Fold, simplify, render — and rewrite `FilterConverter` on top of them

**Files:**
- Create: `api/Services/Opath/OpathFolder.cs`
- Create: `api/Services/Opath/ODataRenderer.cs`
- Create: `api/Services/Opath/PlainLanguageRenderer.cs`
- Modify: `api/DTOs/FilterConversionResult.cs`
- Rewrite: `api/Services/FilterConverter.cs`
- Create: `tests/AFHSync.Tests.Unit/Fixtures/ddg-recipient-filters.json` (copy of the spec fixture)
- Modify: `tests/AFHSync.Tests.Unit/AFHSync.Tests.Unit.csproj`
- Modify: `tests/AFHSync.Tests.Unit/Api/FilterConverterTests.cs`

**Interfaces:**
- Consumes: `OpathParser.Parse`, AST from Task 1.
- Produces: `FilterConversionResult(bool Success, string Filter, string? Warning = null, IReadOnlyList<string>? UnknownAttributes = null)`; `FilterConverter.AttributeMap` and `FilterConverter.PlainNameMap` become `internal static readonly Dictionary<string,string>`; `OpathFolder.Fold(OpathNode, List<string> unknown)`, `OpathFolder.Simplify(OpathNode)`, `ODataRenderer.Render(OpathNode)`, `PlainLanguageRenderer.Render(OpathNode)`.

- [ ] **Step 1: Copy the fixture into the test project and make it ship to the output directory**

Run:
```bash
mkdir -p tests/AFHSync.Tests.Unit/Fixtures
cp docs/superpowers/specs/2026-08-25-ddg-recipient-filters.json tests/AFHSync.Tests.Unit/Fixtures/ddg-recipient-filters.json
```

Then add to `tests/AFHSync.Tests.Unit/AFHSync.Tests.Unit.csproj`, as a new `<ItemGroup>` before `</Project>`:

```xml
  <ItemGroup>
    <None Update="Fixtures\**\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: Update the existing converter tests and add the new ones**

In `tests/AFHSync.Tests.Unit/Api/FilterConverterTests.cs` replace the test `Convert_UnsupportedAttribute_ReturnsSuccessWithWarning` (Test 8) with:

```csharp
    // Test 8: Unsupported attribute is a FAILURE — a filter Graph will reject must never be
    // stored or used. (Previously a warning with Success=true; that is how the Avalon
    // target set silently collapsed to 6 mailboxes.)
    [Fact]
    public void Convert_UnsupportedAttribute_ReturnsFailureWithWarning()
    {
        var result = _converter.Convert("(SomeUnknownAttr -eq 'Value')");

        Assert.False(result.Success);
        Assert.NotNull(result.Warning);
        Assert.Contains("SomeUnknownAttr", result.Warning);
        Assert.Contains("SomeUnknownAttr", result.UnknownAttributes!);
    }
```

Append these tests inside the class (before the final `}`):

```csharp
    // ---- Real tenant filters (captured 2026-08-25) -------------------------------------

    private static IReadOnlyList<(string Name, string Filter)> LoadFixtures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ddg-recipient-filters.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("displayName").GetString()!, e.GetProperty("recipientFilter").GetString()!))
            .ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex ExchangeOnlyAttribute = new(
        @"RecipientTypeDetails|RecipientType\b|HiddenFromAddressListsEnabled|\bName\b|MailboxPlan|SystemMailbox",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    [Fact]
    public void Convert_AllTenantFixtures_SucceedWithoutExchangeOnlyAttributes()
    {
        var fixtures = LoadFixtures();
        Assert.Equal(16, fixtures.Count);

        foreach (var (name, filter) in fixtures)
        {
            var result = _converter.Convert(filter);

            Assert.True(result.Success, $"{name}: {result.Warning}");
            Assert.DoesNotMatch(ExchangeOnlyAttribute, result.Filter);
            Assert.Null(result.Warning);
        }
    }

    [Theory]
    [InlineData("Buckhead Staff", "officeLocation eq 'Buckhead' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("Intown Staff", "officeLocation eq 'Intown' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("Cobb Staff", "officeLocation eq 'Cobb' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("North Atlanta Staff", "officeLocation eq 'North Atlanta' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("Blue Ridge Staff", "officeLocation eq 'Blue Ridge' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("Clayton Staff", "officeLocation eq 'Clayton' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("North Atlanta Office", "officeLocation eq 'North Atlanta' or (onPremisesExtensionAttributes/extensionAttribute2 eq 'AFH' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff')")]
    [InlineData("All Atlanta Fine Homes Staff", "onPremisesExtensionAttributes/extensionAttribute2 eq 'AFH' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("All Mountain Staff", "onPremisesExtensionAttributes/extensionAttribute2 eq 'MSIR' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    public void Convert_TenantFixture_ProducesExpectedGraphFilter(string name, string expected)
    {
        var (_, filter) = LoadFixtures().Single(f => f.Name == name);

        var result = _converter.Convert(filter);

        Assert.True(result.Success, result.Warning);
        Assert.Equal(expected, result.Filter);
    }

    // ---- Folding rules ------------------------------------------------------------------

    [Fact]
    public void Convert_RecipientTypeDetailsOrGroup_IsFoldedAway()
    {
        var result = _converter.Convert(
            "((Office -eq 'Buckhead') -and (((RecipientTypeDetails -eq 'UserMailbox') -or (RecipientTypeDetails -eq 'SharedMailbox'))))");

        Assert.True(result.Success);
        Assert.Equal("officeLocation eq 'Buckhead'", result.Filter);
    }

    [Fact]
    public void Convert_MailContactBranch_IsDropped()
    {
        var result = _converter.Convert(
            "((Office -eq 'Buckhead') -and (RecipientTypeDetails -eq 'UserMailbox')) -or ((RecipientTypeDetails -eq 'MailContact') -and (CustomAttribute4 -eq 'DDL'))");

        Assert.True(result.Success);
        Assert.Equal("officeLocation eq 'Buckhead'", result.Filter);
    }

    [Fact]
    public void Convert_ExchangeSystemExclusions_AreFoldedAway()
    {
        var result = _converter.Convert(
            "(Office -eq 'Buckhead') -and (-not(Name -like 'SystemMailbox{*')) -and (-not(RecipientTypeDetailsValue -eq 'MailboxPlan'))");

        Assert.True(result.Success);
        Assert.Equal("officeLocation eq 'Buckhead'", result.Filter);
    }

    [Fact]
    public void Convert_FilterThatFoldsToAllUsers_Fails()
    {
        var result = _converter.Convert("(RecipientTypeDetails -eq 'UserMailbox') -and (HiddenFromAddressListsEnabled -eq 'False')");

        Assert.False(result.Success);
        Assert.Contains("all users", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Convert_FilterThatFoldsToNoUsers_Fails()
    {
        var result = _converter.Convert("(RecipientTypeDetails -eq 'MailContact') -and (Office -eq 'Buckhead')");

        Assert.False(result.Success);
        Assert.Contains("no users", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Value safety and operators -----------------------------------------------------

    [Fact]
    public void Convert_AttributeNameInsideQuotedValue_IsNotRewritten()
    {
        var result = _converter.Convert("(Title -like 'Office Manager*') -and (Department -eq 'Sales Office')");

        Assert.True(result.Success);
        Assert.Equal("startsWith(jobTitle, 'Office Manager') and department eq 'Sales Office'", result.Filter);
    }

    [Theory]
    [InlineData("(Title -like 'Agent*')", "startsWith(jobTitle, 'Agent')")]
    [InlineData("(Title -like '*Agent')", "endsWith(jobTitle, 'Agent')")]
    [InlineData("(Title -like '*Agent*')", "contains(jobTitle, 'Agent')")]
    [InlineData("(Title -like 'Agent')", "jobTitle eq 'Agent'")]
    [InlineData("(Title -notlike 'Agent*')", "not(startsWith(jobTitle, 'Agent'))")]
    public void Convert_LikeOperators_MapToODataFunctions(string opath, string expected)
    {
        var result = _converter.Convert(opath);

        Assert.True(result.Success, result.Warning);
        Assert.Equal(expected, result.Filter);
    }

    [Fact]
    public void Convert_QuoteInValue_IsEscapedForOData()
    {
        var result = _converter.Convert("(Company -eq 'Sotheby''s')");

        Assert.True(result.Success);
        Assert.Equal("companyName eq 'Sotheby''s'", result.Filter);
    }

    [Fact]
    public void Convert_UnparseableFilter_Fails()
    {
        var result = _converter.Convert("(Office -eq 'Buckhead'");

        Assert.False(result.Success);
        Assert.Contains("parsed", result.Warning);
    }

    [Fact]
    public void ToPlainLanguage_DropsExchangeOnlyClauses_AndKeepsValuesIntact()
    {
        var (_, filter) = LoadFixtures().Single(f => f.Name == "Buckhead Staff");

        var plain = _converter.ToPlainLanguage(filter);

        Assert.Equal("Office = Buckhead AND Role = Staff", plain);
    }

    [Fact]
    public void ToPlainLanguage_UnparseableInput_FallsBackToTextReplacement()
    {
        var plain = _converter.ToPlainLanguage("(Office -eq 'Buckhead'");

        Assert.Contains("Office = Buckhead", plain);
    }
```

- [ ] **Step 3: Run the converter tests to verify the new ones fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~FilterConverterTests" 2>&1 | tail -3`
Expected: build error on `result.UnknownAttributes` (property does not exist).

- [ ] **Step 4: Extend the result record**

Replace `api/DTOs/FilterConversionResult.cs` with:

```csharp
namespace AFHSync.Api.DTOs;

/// <summary>
/// Outcome of converting an Exchange OPATH RecipientFilter to a Graph OData $filter.
/// <see cref="Success"/> is false when the filter could not be parsed, when any attribute
/// with no Graph equivalent remains after folding Exchange-only predicates, or when the
/// filter collapses to a constant (matches all / no users). A false result must never be
/// stored as a source filter or sent to Graph.
/// </summary>
public record FilterConversionResult(
    bool Success,
    string Filter,
    string? Warning = null,
    IReadOnlyList<string>? UnknownAttributes = null
);
```

- [ ] **Step 5: Create the folder/simplifier**

Create `api/Services/Opath/OpathFolder.cs`:

```csharp
namespace AFHSync.Api.Services.Opath;

/// <summary>
/// Turns Exchange-only predicates into boolean constants (from a Graph *user*'s point of view)
/// and simplifies the tree. After <see cref="Simplify"/>, a tree that still contains a
/// <see cref="OpathCompare"/> on an attribute outside <see cref="FilterConverter.AttributeMap"/>
/// has been reported through the <c>unknown</c> list.
/// </summary>
internal static class OpathFolder
{
    /// <summary>Recipient types that correspond to a Graph user object.</summary>
    private static readonly HashSet<string> UserRecipientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "UserMailbox", "SharedMailbox", "RoomMailbox", "EquipmentMailbox",
        "MailUser", "LinkedMailbox", "TeamMailbox", "LegacyMailbox"
    };

    public static OpathNode Fold(OpathNode node, List<string> unknown) => node switch
    {
        OpathCompare c => FoldCompare(c, unknown),
        OpathNot n => new OpathNot(Fold(n.Inner, unknown)),
        OpathAnd a => new OpathAnd(Fold(a.Left, unknown), Fold(a.Right, unknown)),
        OpathOr o => new OpathOr(Fold(o.Left, unknown), Fold(o.Right, unknown)),
        _ => node
    };

    private static OpathNode FoldCompare(OpathCompare c, List<string> unknown)
    {
        var attr = c.Attribute;

        if (Is(attr, "RecipientTypeDetails") || Is(attr, "RecipientType"))
        {
            bool isUser = UserRecipientTypes.Contains(c.Value);
            switch (c.Operator)
            {
                case "eq": return new OpathConst(isUser);
                case "ne": return new OpathConst(!isUser);
                default:
                    unknown.Add($"{attr} -{c.Operator}");
                    return c;
            }
        }

        // Only ever appears as -not(RecipientTypeDetailsValue -eq 'MailboxPlan') style exclusions.
        if (Is(attr, "RecipientTypeDetailsValue"))
            return new OpathConst(false);

        // -not(Name -like 'SystemMailbox{*') / 'CAS_{*' exclusions. Name is not a Graph property.
        if (Is(attr, "Name") && c.Operator is "like" or "notlike")
            return new OpathConst(c.Operator == "notlike");

        // GAL visibility is filtered client-side in SourceResolver.
        if (Is(attr, "HiddenFromAddressListsEnabled"))
            return new OpathConst(true);

        if (FilterConverter.AttributeMap.ContainsKey(attr))
            return c;

        unknown.Add(attr);
        return c;
    }

    private static bool Is(string attr, string name) => attr.Equals(name, StringComparison.OrdinalIgnoreCase);

    public static OpathNode Simplify(OpathNode node)
    {
        while (true)
        {
            var next = SimplifyOnce(node);
            if (next == node) return next;
            node = next;
        }
    }

    private static OpathNode SimplifyOnce(OpathNode node)
    {
        switch (node)
        {
            case OpathAnd a:
            {
                var l = SimplifyOnce(a.Left);
                var r = SimplifyOnce(a.Right);
                if (l is OpathConst { Value: false } || r is OpathConst { Value: false }) return new OpathConst(false);
                if (l is OpathConst { Value: true }) return r;
                if (r is OpathConst { Value: true }) return l;
                return ReferenceEquals(l, a.Left) && ReferenceEquals(r, a.Right) ? a : new OpathAnd(l, r);
            }
            case OpathOr o:
            {
                var l = SimplifyOnce(o.Left);
                var r = SimplifyOnce(o.Right);
                if (l is OpathConst { Value: true } || r is OpathConst { Value: true }) return new OpathConst(true);
                if (l is OpathConst { Value: false }) return r;
                if (r is OpathConst { Value: false }) return l;
                return ReferenceEquals(l, o.Left) && ReferenceEquals(r, o.Right) ? o : new OpathOr(l, r);
            }
            case OpathNot n:
            {
                var inner = SimplifyOnce(n.Inner);
                if (inner is OpathConst c) return new OpathConst(!c.Value);
                if (inner is OpathNot nn) return nn.Inner;
                return ReferenceEquals(inner, n.Inner) ? n : new OpathNot(inner);
            }
            default:
                return node;
        }
    }
}
```

- [ ] **Step 6: Create the OData renderer**

Create `api/Services/Opath/ODataRenderer.cs`:

```csharp
namespace AFHSync.Api.Services.Opath;

/// <summary>
/// Renders a folded/simplified AST as a Graph OData $filter. Attribute names are mapped
/// through <see cref="FilterConverter.AttributeMap"/> on <see cref="OpathCompare"/> nodes only,
/// so literal values are never rewritten. Mixed and/or children are parenthesized.
/// </summary>
internal static class ODataRenderer
{
    public static string Render(OpathNode node) => node switch
    {
        OpathAnd a => $"{Operand(a.Left, typeof(OpathAnd))} and {Operand(a.Right, typeof(OpathAnd))}",
        OpathOr o => $"{Operand(o.Left, typeof(OpathOr))} or {Operand(o.Right, typeof(OpathOr))}",
        OpathNot n => $"not({Render(n.Inner)})",
        OpathCompare c => RenderCompare(c),
        OpathConst k => k.Value ? "true" : "false",
        _ => throw new InvalidOperationException($"Unknown node {node.GetType().Name}")
    };

    private static string Operand(OpathNode child, Type parent)
    {
        bool needsParens = (child is OpathAnd || child is OpathOr) && child.GetType() != parent;
        return needsParens ? $"({Render(child)})" : Render(child);
    }

    private static string RenderCompare(OpathCompare c)
    {
        var field = FilterConverter.AttributeMap[c.Attribute];
        return c.Operator switch
        {
            "eq" => $"{field} eq '{Escape(c.Value)}'",
            "ne" => $"{field} ne '{Escape(c.Value)}'",
            "gt" or "lt" or "ge" or "le" => $"{field} {c.Operator} '{Escape(c.Value)}'",
            "like" => RenderLike(field, c.Value),
            "notlike" => $"not({RenderLike(field, c.Value)})",
            _ => throw new InvalidOperationException($"Unsupported operator {c.Operator}")
        };
    }

    private static string RenderLike(string field, string pattern)
    {
        bool leading = pattern.StartsWith('*');
        bool trailing = pattern.Length > 1 && pattern.EndsWith('*');
        var core = Escape(pattern.Trim('*'));
        if (leading && trailing) return $"contains({field}, '{core}')";
        if (trailing) return $"startsWith({field}, '{core}')";
        if (leading) return $"endsWith({field}, '{core}')";
        return $"{field} eq '{core}'";
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
```

- [ ] **Step 7: Create the plain-language renderer**

Create `api/Services/Opath/PlainLanguageRenderer.cs`:

```csharp
namespace AFHSync.Api.Services.Opath;

/// <summary>Renders a folded/simplified AST for humans: <c>Office = Buckhead AND Role = Staff</c>.</summary>
internal static class PlainLanguageRenderer
{
    public static string Render(OpathNode node) => node switch
    {
        OpathAnd a => $"{Operand(a.Left, typeof(OpathAnd))} AND {Operand(a.Right, typeof(OpathAnd))}",
        OpathOr o => $"{Operand(o.Left, typeof(OpathOr))} OR {Operand(o.Right, typeof(OpathOr))}",
        OpathNot n => $"NOT ({Render(n.Inner)})",
        OpathCompare c => $"{Name(c.Attribute)} {Op(c.Operator)} {c.Value}",
        OpathConst k => k.Value ? "everyone" : "no one",
        _ => throw new InvalidOperationException($"Unknown node {node.GetType().Name}")
    };

    private static string Operand(OpathNode child, Type parent)
    {
        bool needsParens = (child is OpathAnd || child is OpathOr) && child.GetType() != parent;
        return needsParens ? $"({Render(child)})" : Render(child);
    }

    private static string Name(string attr) =>
        FilterConverter.PlainNameMap.TryGetValue(attr, out var plain) ? plain : attr;

    private static string Op(string op) => op switch
    {
        "eq" => "=",
        "ne" => "!=",
        "like" => "LIKE",
        "notlike" => "NOT LIKE",
        "gt" => ">",
        "lt" => "<",
        "ge" => ">=",
        "le" => "<=",
        _ => op
    };
}
```

- [ ] **Step 8: Rewrite `FilterConverter`**

Replace the whole of `api/Services/FilterConverter.cs` with:

```csharp
namespace AFHSync.Api.Services;

using System.Text.RegularExpressions;
using AFHSync.Api.DTOs;
using AFHSync.Api.Services.Opath;
using Microsoft.Extensions.Logging;

/// <summary>
/// Converts Exchange OPATH RecipientFilter syntax to Microsoft Graph OData $filter syntax.
///
/// Pipeline: parse (OpathParser) → fold Exchange-only predicates to constants (OpathFolder.Fold)
/// → simplify (OpathFolder.Simplify) → render (ODataRenderer).
///
/// A filter that still references an attribute with no Graph equivalent, that cannot be parsed,
/// or that collapses to "everyone"/"no one" is a FAILURE (Success=false). Callers must not store
/// or query with a failed result — Graph rejects such filters with Request_UnsupportedQuery,
/// which is how a DDG silently resolves to zero members.
/// </summary>
public class FilterConverter : IFilterConverter
{
    private readonly ILogger<FilterConverter> _logger;

    // AFH-specific OPATH attribute -> OData field mapping (case-insensitive)
    internal static readonly Dictionary<string, string> AttributeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Office"] = "officeLocation",
        ["CustomAttribute1"] = "onPremisesExtensionAttributes/extensionAttribute1",
        ["CustomAttribute2"] = "onPremisesExtensionAttributes/extensionAttribute2",
        ["CustomAttribute3"] = "onPremisesExtensionAttributes/extensionAttribute3",
        ["CustomAttribute4"] = "onPremisesExtensionAttributes/extensionAttribute4",
        ["CustomAttribute5"] = "onPremisesExtensionAttributes/extensionAttribute5",
        ["Department"] = "department",
        ["DisplayName"] = "displayName",
        ["Title"] = "jobTitle",
        ["Company"] = "companyName",
        ["City"] = "city",
        ["State"] = "state",
        ["Country"] = "country",
    };

    // Human-readable display names for ToPlainLanguage
    internal static readonly Dictionary<string, string> PlainNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Office"] = "Office",
        ["CustomAttribute1"] = "Custom1",
        ["CustomAttribute2"] = "Brand",
        ["CustomAttribute3"] = "Role",
        ["CustomAttribute4"] = "Department Code",
        ["CustomAttribute5"] = "Custom5",
        ["Department"] = "Department",
        ["DisplayName"] = "Name",
        ["Title"] = "Title",
        ["Company"] = "Company",
        ["City"] = "City",
        ["State"] = "State",
        ["Country"] = "Country",
    };

    public FilterConverter(ILogger<FilterConverter> logger)
    {
        _logger = logger;
    }

    public FilterConversionResult Convert(string opathFilter)
    {
        if (string.IsNullOrWhiteSpace(opathFilter))
            return new FilterConversionResult(false, opathFilter ?? string.Empty, "Filter is empty or null", []);

        OpathNode ast;
        try
        {
            ast = OpathParser.Parse(opathFilter.Trim());
        }
        catch (OpathParseException ex)
        {
            _logger.LogWarning("OPATH filter could not be parsed: {Message}. Filter: {Filter}", ex.Message, opathFilter);
            return new FilterConversionResult(false, opathFilter, $"Filter could not be parsed: {ex.Message}", []);
        }

        var unknown = new List<string>();
        var simplified = OpathFolder.Simplify(OpathFolder.Fold(ast, unknown));

        if (unknown.Count > 0)
        {
            var distinct = unknown.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _logger.LogWarning("OPATH filter uses attribute(s) with no Graph equivalent: {Attrs}. Filter: {Filter}",
                string.Join(", ", distinct), opathFilter);
            return new FilterConversionResult(false, opathFilter,
                $"Unsupported attribute(s): {string.Join(", ", distinct)} — this filter cannot be evaluated by Graph",
                distinct);
        }

        if (simplified is OpathConst constant)
        {
            return new FilterConversionResult(false, opathFilter,
                constant.Value
                    ? "Filter matches all users once Exchange-only conditions are removed; a source filter must be selective"
                    : "Filter matches no users (it only selects non-user recipients such as mail contacts)",
                []);
        }

        return new FilterConversionResult(true, ODataRenderer.Render(simplified), null, []);
    }

    public string ToPlainLanguage(string opathFilter)
    {
        if (string.IsNullOrWhiteSpace(opathFilter))
            return string.Empty;

        try
        {
            var ast = OpathParser.Parse(opathFilter.Trim());
            var simplified = OpathFolder.Simplify(OpathFolder.Fold(ast, new List<string>()));
            return PlainLanguageRenderer.Render(simplified);
        }
        catch (OpathParseException)
        {
            return ToPlainLanguageLegacy(opathFilter);
        }
    }

    /// <summary>Text-replacement fallback for filters the parser rejects (kept so the UI never shows nothing).</summary>
    private static string ToPlainLanguageLegacy(string opathFilter)
    {
        var plain = opathFilter.Trim();
        foreach (var (opathAttr, displayName) in PlainNameMap)
        {
            plain = Regex.Replace(plain, $@"\b{Regex.Escape(opathAttr)}\b", displayName, RegexOptions.IgnoreCase);
        }
        plain = Regex.Replace(plain, @"\s+-eq\s+", " = ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-ne\s+", " != ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-and\s+", " AND ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-or\s+", " OR ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-not\s+", " NOT ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-like\s+", " LIKE ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"^\(([^()]*)\)$", "$1");
        return plain.Replace("'", "");
    }
}
```

- [ ] **Step 9: Run the converter + parser tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~FilterConverterTests|FullyQualifiedName~OpathParserTests" 2>&1 | tail -5`
Expected: all pass. If a golden string in `Convert_TenantFixture_ProducesExpectedGraphFilter` differs only by parenthesization, inspect the fixture's actual OPATH before changing the expected value — the expectations above were derived by hand from the real filters; the code must match them, not the other way round.

- [ ] **Step 10: Run the full unit suite (other tests construct `FilterConversionResult` with two args — must still compile)**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!`.

- [ ] **Step 11: Commit**

```bash
git add api/Services api/DTOs/FilterConversionResult.cs tests/AFHSync.Tests.Unit
git commit -m "feat(filter-converter): parse OPATH, fold Exchange-only predicates, fail on unknown attributes

All 16 tenant DDG filters now convert; RecipientTypeDetails OR-groups and MailContact/DDL
branches no longer leak into Graph \$filter. Unknown attributes are a failure, not a warning.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: `RefreshDdg` refuses to store a failed conversion

**Files:**
- Modify: `api/Controllers/TunnelsController.cs:577-585`
- Modify: `api/Controllers/GraphController.cs:296`

**Interfaces:**
- Consumes: `FilterConversionResult.Success/Warning` (Task 2).

- [ ] **Step 1: Replace the conversion block in `RefreshDdg`**

In `api/Controllers/TunnelsController.cs`, replace:

```csharp
        var conversionResult = _filterConverter.Convert(ddgInfo.RecipientFilter);

        source.SourceIdentifier = conversionResult.Filter ?? source.SourceIdentifier;
        source.SourceFilterPlain = _filterConverter.ToPlainLanguage(ddgInfo.RecipientFilter);
        source.SourceDisplayName = ddgInfo.DisplayName;
```

with:

```csharp
        var conversionResult = _filterConverter.Convert(ddgInfo.RecipientFilter);
        if (!conversionResult.Success)
        {
            _logger.LogWarning(
                "Refresh DDG for tunnel {TunnelId} source {SourceId} refused: {Warning}. Existing filter kept.",
                id, sourceId, conversionResult.Warning);
            return UnprocessableEntity(new
            {
                message = $"The DDG's recipient filter cannot be converted to a Graph filter: {conversionResult.Warning}. The source was left unchanged."
            });
        }

        source.SourceIdentifier = conversionResult.Filter;
        source.SourceFilterPlain = _filterConverter.ToPlainLanguage(ddgInfo.RecipientFilter);
        source.SourceDisplayName = ddgInfo.DisplayName;
```

(`_logger` already exists — `api/Controllers/TunnelsController.cs:20`.)

- [ ] **Step 2: `GraphController` returns `graphFilter: null` when conversion failed**

In `api/Controllers/GraphController.cs` `EnrichDdgAsync` (around line 296), change
```csharp
            GraphFilter: conversion.Filter,
```
to
```csharp
            GraphFilter: conversion.Success ? conversion.Filter : null,
```
so the UI never receives the raw OPATH text in the `graphFilter` field.

- [ ] **Step 3: Build the API**

Run: `dotnet build api --nologo -v quiet 2>&1 | tail -3`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add api/Controllers/TunnelsController.cs api/Controllers/GraphController.cs
git commit -m "fix(api): refresh-ddg returns 422 on failed conversion; DDG DTO graphFilter is null on failure

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: `TargetFilterResolver` reports DDG failures

**Files:**
- Modify: `worker/Services/TargetFilterResolver.cs`
- Modify: `tests/AFHSync.Tests.Unit/Sync/TargetFilterResolverTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record DdgTargetFailure(string Id, string? DisplayName, string Reason);
  public sealed record TargetFilterResolution(HashSet<string> Emails, IReadOnlyList<DdgTargetFailure> Failures);
  // TargetFilterResolver.ResolveAsync(...) now returns Task<TargetFilterResolution>
  ```
  Both records live in `worker/Services/TargetFilterResolver.cs`, namespace `AFHSync.Worker.Services`, `internal` like the resolver (the test project already has `InternalsVisibleTo`, or the class is already `internal static` and tested — keep the same visibility as `TargetFilterResolver`).

- [ ] **Step 1: Update the existing tests to the new return shape and add failure tests**

In `tests/AFHSync.Tests.Unit/Sync/TargetFilterResolverTests.cs`, every existing assertion of the form `result.Count` / `Assert.Contains("x", result)` / `Assert.Empty(result)` must now read from `result.Emails` (e.g. `Assert.Equal(2, result.Emails.Count); Assert.Contains("a@x.com", result.Emails);`). Do this with a careful read of each test; there are ten `[Fact]`s.

Then append inside the class:

```csharp
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
```

- [ ] **Step 2: Run to verify they fail to compile**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~TargetFilterResolverTests" 2>&1 | tail -3`
Expected: build error: `'HashSet<string>' does not contain a definition for 'Emails'`.

- [ ] **Step 3: Change the resolver**

In `worker/Services/TargetFilterResolver.cs`:

Add directly above `internal static class TargetFilterResolver`:

```csharp
/// <summary>One DDG target that could not contribute members this run.</summary>
internal sealed record DdgTargetFailure(string Id, string? DisplayName, string Reason);

/// <summary>Resolved target emails plus every DDG that failed to resolve.</summary>
internal sealed record TargetFilterResolution(HashSet<string> Emails, IReadOnlyList<DdgTargetFailure> Failures);
```

Change the signature `public static async Task<HashSet<string>> ResolveAsync(` to `public static async Task<TargetFilterResolution> ResolveAsync(`.

Inside the method: declare `var failures = new List<DdgTargetFailure>();` right after `var plEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);`. Replace every `return plEmails;` with `return new TargetFilterResolution(plEmails, failures);`.

Then in the DDG loop make these four edits:

1. Where `ddgInfo == null` (log "not found ... skipping"): add `failures.Add(new DdgTargetFailure(id, displayName, "DDG not found in Exchange at sync time"));` before `continue;`.
2. Where `!conversion.Success || string.IsNullOrWhiteSpace(conversion.Filter)`: add `failures.Add(new DdgTargetFailure(id, displayName, $"recipient filter could not be converted to a Graph filter: {conversion.Warning ?? "unknown"}"));` before `continue;`.
3. Where `members.Count == 0`: add `failures.Add(new DdgTargetFailure(id, displayName, "resolved to 0 members"));` inside that branch.
4. In the `catch (Exception ex)`: add `failures.Add(new DdgTargetFailure(id, displayName, $"resolution failed: {ex.Message}"));`.

Also change the per-DDG log calls at (1), (2), (4) from `LogWarning` to `LogError` — a DDG target that silently contributes nothing is an error for the run.

- [ ] **Step 4: Fix the call site in `SyncEngine` so the worker compiles (behavioural change is Task 5)**

In `worker/Services/SyncEngine.cs` at the `plEmails = await TargetFilterResolver.ResolveAsync(` call (around line 382), change it to:

```csharp
                    var resolution = await TargetFilterResolver.ResolveAsync(
                        canonicalPl.TargetUserFilter,
                        ddgResolver,
                        filterConverter,
                        (graphFilter, innerCt) => QueryDdgMemberEmailsAsync(graphFilter, tunnel.Name, innerCt),
                        logger,
                        ct);
                    plEmails = resolution.Emails;
```

- [ ] **Step 5: Run the resolver tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~TargetFilterResolverTests" 2>&1 | tail -3`
Expected: `Passed!` (7 updated + 5 new).

- [ ] **Step 6: Commit**

```bash
git add worker/Services/TargetFilterResolver.cs worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/TargetFilterResolverTests.cs
git commit -m "feat(worker): TargetFilterResolver reports per-DDG failures instead of swallowing them

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: `SyncEngine` records DDG failures and never widens an empty SpecificUsers scope

**Files:**
- Modify: `worker/Services/SyncEngine.cs:364-447` (Step 5e block) and the `int created = 0, updated = 0, …` declaration after Step 5g
- Modify: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`

**Interfaces:**
- Consumes: `TargetFilterResolution` (Task 4); `IRunLogger.AddItem(SyncRunItem)`.
- Behaviour: each `DdgTargetFailure` ⇒ one `SyncRunItem { Action="failed", TunnelId, PhoneListId, ErrorMessage="DDG target '{name}': {reason}" }` and +1 to the tunnel's `failed` count (so the tunnel is counted as warned and the run ends `Warning`). When the phone list scope is `SpecificUsers` and the resolved email set is empty, the tunnel processes **zero** mailboxes (today it silently falls through to *all* active mailboxes).

- [ ] **Step 1: Add the failing engine test**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`, first extend the `CreateEngine` helper with two optional parameters so the DDG services can be injected:

```csharp
    private static SyncEngine CreateEngine(
        string dbName,
        FakeSourceResolver? sourceResolver = null,
        FakeContactPayloadBuilder? payloadBuilder = null,
        FakeContactWriter? contactWriter = null,
        FakeContactFolderManager? folderManager = null,
        IStaleContactHandler? staleHandler = null,
        FakeRunLogger? runLogger = null,
        ThrottleCounter? throttleCounter = null,
        FakePhotoSyncService? photoSyncService = null,
        AFHSync.Api.Services.IDDGResolver? ddgResolver = null,
        AFHSync.Api.Services.IFilterConverter? filterConverter = null)
    {
        return new SyncEngine(
            CreateFactory(dbName),
            sourceResolver ?? new FakeSourceResolver([]),
            payloadBuilder ?? new FakeContactPayloadBuilder(),
            contactWriter ?? new FakeContactWriter(),
            folderManager ?? new FakeContactFolderManager(),
            staleHandler ?? new FakeStaleContactHandler(),
            runLogger ?? new FakeRunLogger(),
            throttleCounter ?? new ThrottleCounter(),
            photoSyncService ?? new FakePhotoSyncService(),
            null!, // GraphClientFactory — not used in unit tests
            CreateEmptyConfig(),
            NullLogger<SyncEngine>.Instance,
            ddgResolver!,
            filterConverter!);
    }
```

Add these two fakes next to the other private fakes at the bottom of the class:

```csharp
    /// <summary>Every DDG lookup returns null ("not found").</summary>
    private sealed class NotFoundDdgResolver : AFHSync.Api.Services.IDDGResolver
    {
        public Task<IReadOnlyList<AFHSync.Api.Services.DdgInfo>> ListDdgsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AFHSync.Api.Services.DdgInfo>>([]);

        public Task<AFHSync.Api.Services.DdgInfo?> GetDdgAsync(string identity, CancellationToken ct = default)
            => Task.FromResult<AFHSync.Api.Services.DdgInfo?>(null);
    }

    private sealed class PassThroughFilterConverter : AFHSync.Api.Services.IFilterConverter
    {
        public AFHSync.Api.DTOs.FilterConversionResult Convert(string opathFilter)
            => new(true, opathFilter);

        public string ToPlainLanguage(string opathFilter) => opathFilter;
    }
```

Add the test (after Test 2):

```csharp
    // ==============================
    // Phase 1: a DDG target that fails to resolve is recorded as a failed run item,
    // and an all-DDG SpecificUsers list that resolves to nothing targets NO mailboxes.
    // ==============================

    [Fact]
    public async Task RunAsync_DdgTargetFails_RecordsFailedItemAndTargetsNoMailboxes()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var seedCtx = MakeDbContext(dbName))
        {
            var tunnel = new Tunnel
            {
                Id = 1,
                Name = "Avalon Gate Code",
                Status = TunnelStatus.Active,
                StalePolicy = StalePolicy.AutoRemove,
                StaleHoldDays = 14,
            };
            var phoneList = new PhoneList
            {
                Id = 12,
                Name = "Avalon Users",
                TargetScope = TargetScope.SpecificUsers,
                TargetUserFilter = """{"ddgs":[{"id":"ddg-broken","displayName":"Buckhead Staff"}]}""",
            };
            var tunnelPhoneList = new TunnelPhoneList { TunnelId = 1, PhoneListId = 12, Tunnel = tunnel, PhoneList = phoneList };
            tunnel.TunnelPhoneLists.Add(tunnelPhoneList);
            seedCtx.Tunnels.Add(tunnel);
            seedCtx.PhoneLists.Add(phoneList);
            seedCtx.TunnelPhoneLists.Add(tunnelPhoneList);
            // An active mailbox that must NOT be processed, because the scope resolved to nothing.
            seedCtx.TargetMailboxes.Add(new TargetMailbox
            {
                Id = 1, EntraId = "mb-1", Email = "someone@x.com", IsActive = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }

        var sourceUser = new SourceUser
        {
            Id = 1, EntraId = "src-1", DisplayName = "Avalon Gate Code", Email = "avalon@x.com",
            IsEnabled = true, LastFetchedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var runLogger = new FakeRunLogger();
        var contactWriter = new FakeContactWriter();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([sourceUser]),
            contactWriter: contactWriter,
            runLogger: runLogger,
            ddgResolver: new NotFoundDdgResolver(),
            filterConverter: new PassThroughFilterConverter());

        var run = await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        var failedItem = Assert.Single(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Equal(1, failedItem.TunnelId);
        Assert.Contains("Buckhead Staff", failedItem.ErrorMessage);
        Assert.Contains("not found", failedItem.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runLogger.AddedItems, i => i.Action == "created");
        // FakeRunLogger records the finalize counters; the DDG failure is counted as a contact failure
        // so the tunnel is 'warned' and the run ends Warning (see DetermineStatus).
        Assert.True(runLogger.FinalizedFailed >= 1, "DDG failure must be counted as a failure");
        Assert.NotNull(run);
    }
```

- [ ] **Step 2: Run the new test and verify it fails**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~RunAsync_DdgTargetFails" 2>&1 | tail -8`
Expected: FAIL — `Assert.Single` finds no "failed" item (and/or a "created" item exists because the empty scope fell through to all mailboxes).

- [ ] **Step 3: Implement in `SyncEngine.ProcessTunnelAsync`**

Immediately before the `// Step 5e:` comment, add:

```csharp
        // Phase 1: DDG target failures are recorded as run items and count as tunnel failures.
        int ddgTargetFailures = 0;
```

Inside the Step 5e `try`, right after `plEmails = resolution.Emails;` (from Task 4), add:

```csharp
                    foreach (var failure in resolution.Failures)
                    {
                        var ddgName = failure.DisplayName ?? failure.Id;
                        logger.LogError(
                            "Tunnel {TunnelName}: DDG target '{Ddg}' failed: {Reason}",
                            tunnel.Name, ddgName, failure.Reason);
                        runLogger.AddItem(new SyncRunItem
                        {
                            SyncRunId = run.Id,
                            TunnelId = tunnel.Id,
                            PhoneListId = canonicalPl.Id,
                            Action = "failed",
                            ErrorMessage = $"DDG target '{ddgName}': {failure.Reason}",
                            CreatedAt = DateTime.UtcNow
                        });
                        ddgTargetFailures++;
                    }
```

Replace the `if (plEmails.Count > 0) { … }` block's *closing* behaviour so an empty result no longer falls through. Change:

```csharp
                if (plEmails.Count > 0)
                {
```

to:

```csharp
                if (plEmails.Count == 0)
                {
                    logger.LogError(
                        "Tunnel {TunnelName}: phone list '{PhoneList}' is scoped to specific users but resolved to 0 emails — no mailboxes will be processed",
                        tunnel.Name, canonicalPl.Name);
                    targetMailboxes = [];
                }
                else
                {
```

(The existing closing brace of the old `if` now closes the `else`.)

Then, after the line `int created = 0, updated = 0, skipped = 0, failed = 0, removed = 0;` (Step 5g), add:

```csharp
        failed += ddgTargetFailures;
```

- [ ] **Step 4: Run the engine tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~SyncEngineTests" 2>&1 | tail -3`
Expected: `Passed!` including `RunAsync_DdgTargetFails_RecordsFailedItemAndTargetsNoMailboxes`.

- [ ] **Step 5: Full unit suite**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!`.

- [ ] **Step 6: Commit**

```bash
git add worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -m "fix(worker): record DDG target failures as run items; empty SpecificUsers scope targets no one

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Worker Notes source cleanup (dead `aboutMe` path)

**Files:**
- Modify: `worker/Services/SourceResolver.cs:360-364`
- Modify: `api/Migrations/20260424174537_ResetDataHashesForCloudNotes.cs:8-14` (doc comment only)

- [ ] **Step 1: Replace the Notes mapping**

In `worker/Services/SourceResolver.cs`, replace:

```csharp
            // Cloud-only: Graph's User.aboutMe is the "Notes" field visible in Teams/OWA contact
            // cards and edited via M365 admin center / Entra portal. Falls back to
            // onPremisesExtensionAttributes.extensionAttribute5 for any remaining AD-synced users
            // whose on-prem `info` attribute still flows up via AD Connect.
            Notes = graphUser.AboutMe ?? graphUser.OnPremisesExtensionAttributes?.ExtensionAttribute5,
```

with:

```csharp
            // Notes come from Exchange CustomAttribute5, which Graph exposes as
            // onPremisesExtensionAttributes.extensionAttribute5 (writable for cloud-only users via
            // Set-Mailbox -CustomAttribute5). Graph's User.aboutMe is NOT usable here: it is not
            // selectable on the /users list endpoint, so it was never populated (see 55b980b).
            Notes = graphUser.OnPremisesExtensionAttributes?.ExtensionAttribute5,
```

- [ ] **Step 2: Correct the migration's doc comment**

In `api/Migrations/20260424174537_ResetDataHashesForCloudNotes.cs`, replace the `<summary>` text with:

```csharp
    /// <summary>
    /// Resets contact data hashes to force a re-sync of the Notes field (April 2026).
    /// Historical note: this was written when Notes was expected to come from Graph User.aboutMe;
    /// aboutMe cannot be $select-ed on the /users list endpoint, so Notes is sourced from
    /// Exchange CustomAttribute5 (onPremisesExtensionAttributes.extensionAttribute5).
    /// </summary>
```

- [ ] **Step 3: Build worker and run `SourceResolverTests`**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~SourceResolverTests" 2>&1 | tail -3`
Expected: `Passed!`.

- [ ] **Step 4: Commit**

```bash
git add worker/Services/SourceResolver.cs api/Migrations/20260424174537_ResetDataHashesForCloudNotes.cs
git commit -m "chore(worker): Notes come from CustomAttribute5 only; drop dead aboutMe fallback

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: Frontend — unconvertible DDGs cannot be selected; no raw-OPATH fallback

**Files:**
- Modify: `frontend/src/components/DDGSearchList.tsx`
- Modify: `frontend/src/components/wizard/StepSource.tsx:42-50`
- Modify: `frontend/src/components/TunnelWizard.tsx:143-156`
- Modify: `frontend/src/app/(app)/tunnels/[id]/page.tsx:251-262`
- Modify: `frontend/src/app/(app)/lists/page.tsx:463, 489-496, 600-620`

**Interfaces:**
- Consumes: `DdgDto.graphFilter | graphFilterSuccess | graphFilterWarning` (already in `frontend/src/types/ddg.ts`).

- [ ] **Step 1: `DDGSearchList` — disable and explain**

In `frontend/src/components/DDGSearchList.tsx`, replace the `<CommandItem …>` element with:

```tsx
                {filtered.map((ddg) => {
                  const unusable = !ddg.graphFilterSuccess || !ddg.graphFilter;
                  return (
                    <CommandItem
                      key={ddg.id}
                      value={ddg.id}
                      disabled={unusable}
                      onSelect={() => {
                        if (unusable) return;
                        onSelect(ddg);
                      }}
                      className={cn(
                        'flex items-center justify-between gap-3 py-2.5 px-3',
                        unusable ? 'opacity-60 cursor-not-allowed' : 'cursor-pointer',
                        (selectedId === ddg.id || selectedIds?.includes(ddg.id)) && 'bg-gold/10 border-l-2 border-gold',
                      )}
                    >
                      <div className="flex flex-col min-w-0">
                        <span className="font-medium text-sm truncate">
                          {ddg.displayName}
                        </span>
                        <span className="text-xs text-text-muted truncate">
                          {ddg.primarySmtpAddress}
                        </span>
                        {unusable && (
                          <span className="text-xs text-destructive truncate" title={ddg.graphFilterWarning ?? undefined}>
                            Cannot be used: {ddg.graphFilterWarning ?? 'filter could not be converted'}
                          </span>
                        )}
                      </div>
                      <div className="flex items-center gap-2 shrink-0">
                        <span className="text-xs bg-muted rounded-full px-2 py-0.5">
                          {ddg.memberCount} members
                        </span>
                        <span className="text-xs text-text-muted">
                          {ddg.type}
                        </span>
                      </div>
                    </CommandItem>
                  );
                })}
```

- [ ] **Step 2: `StepSource.handleSelectDdg` — guard**

In `frontend/src/components/wizard/StepSource.tsx`, at the top of `handleSelectDdg` (line 42) add as the first statement:

```tsx
    if (!ddg.graphFilterSuccess || !ddg.graphFilter) return;
```

- [ ] **Step 3: `TunnelWizard.handleSubmit` — remove the fallback and refuse to submit a broken DDG**

In `frontend/src/components/TunnelWizard.tsx`, inside `handleSubmit` right after `if (formData.sources.length === 0) return;` add:

```tsx
    const brokenDdg = formData.sources.find((s) => s.type === 'ddg' && (!s.ddg?.graphFilterSuccess || !s.ddg?.graphFilter));
    if (brokenDdg) {
      toast.error(`"${brokenDdg.ddg?.displayName}" cannot be used as a source: ${brokenDdg.ddg?.graphFilterWarning ?? 'its filter could not be converted'}.`);
      return;
    }
```

and change `sourceIdentifier: s.ddg.graphFilter ?? s.ddg.recipientFilter,` to `sourceIdentifier: s.ddg.graphFilter!,`. If `toast` is not imported in this file, add `import { toast } from 'sonner';`.

- [ ] **Step 4: Tunnel edit page `handleDdgSelect` — guard and no fallback**

In `frontend/src/app/(app)/tunnels/[id]/page.tsx`, replace `handleDdgSelect` (lines 251-262) with:

```tsx
  const handleDdgSelect = (ddg: DdgDto) => {
    if (!ddg.graphFilterSuccess || !ddg.graphFilter) {
      toast.error(`"${ddg.displayName}" cannot be used as a source: ${ddg.graphFilterWarning ?? 'its filter could not be converted'}.`);
      return;
    }
    const newSource: SourceInput = {
      sourceType: 'ddg',
      sourceIdentifier: ddg.graphFilter,
      sourceDisplayName: ddg.displayName,
      sourceSmtpAddress: ddg.primarySmtpAddress,
      sourceFilterPlain: ddg.recipientFilterPlain,
    };
    setEditForm((prev) => ({
      ...prev,
      sources: [...prev.sources, newSource],
    }));
  };
```

- [ ] **Step 5: Lists page Groups tab — keep and use `graphFilterSuccess`**

In `frontend/src/app/(app)/lists/page.tsx`:

Line 463: change the state type to
```tsx
  const [ddgs, setDdgs] = useState<{ id: string; displayName: string; memberCount: number; usable: boolean; warning: string | null }[]>([]);
```

Line 492: change the mapping to
```tsx
        .then((data) => setDdgs(data.map((d) => ({ id: d.primarySmtpAddress, displayName: d.displayName, memberCount: d.memberCount, usable: d.graphFilterSuccess && !!d.graphFilter, warning: d.graphFilterWarning }))))
```

In the Groups tab button (around line 605), change `disabled={addingGroup === ddg.id}` to `disabled={addingGroup === ddg.id || !ddg.usable}` and replace the `<p className="text-xs text-text-muted">{ddg.memberCount} members</p>` line with:

```tsx
                    <p className="text-xs text-text-muted">
                      {ddg.usable ? `${ddg.memberCount} members` : `Cannot be used: ${ddg.warning ?? 'filter could not be converted'}`}
                    </p>
```

(`DDGTargetPicker` further down uses `DDGSearchList`, so it is covered by Step 1.)

- [ ] **Step 6: Build**

Run: `cd frontend && npm run build 2>&1 | tail -8; cd ..`
Expected: build succeeds with no type errors. Fix any unused-import lint errors it reports.

- [ ] **Step 7: Commit**

```bash
git add frontend/src
git commit -m "fix(frontend): DDGs whose filter cannot be converted are unselectable; drop raw-OPATH fallback

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: Frontend — refresh button only for real DDGs, and show the real error

**Files:**
- Modify: `frontend/src/app/(app)/tunnels/[id]/page.tsx:574`
- Modify: `frontend/src/components/DDGRefreshButton.tsx:30-36`

- [ ] **Step 1: Hide the button when the source has no SMTP address**

In `frontend/src/app/(app)/tunnels/[id]/page.tsx`, replace
```tsx
                          <DDGRefreshButton tunnelId={tunnelId} sourceId={src.id} />
```
with
```tsx
                          {src.sourceSmtpAddress && (
                            <DDGRefreshButton tunnelId={tunnelId} sourceId={src.id} />
                          )}
```

- [ ] **Step 2: Surface the server message**

In `frontend/src/components/DDGRefreshButton.tsx`, replace the `onError` handler:

```tsx
      onError: (error: Error) => {
        toast.error(error.message || 'Failed to refresh filter. Please try again.');
      },
```

- [ ] **Step 3: Build and commit**

Run: `cd frontend && npm run build 2>&1 | tail -5; cd ..`
Expected: success.

```bash
git add frontend/src
git commit -m "fix(frontend): hide Refresh DDG for specific-user sources; show real refresh errors

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: Full verification and PR

**Files:** none new.

- [ ] **Step 1: Full backend test run**

Run: `dotnet test --nologo -v quiet 2>&1 | tail -6`
Expected: unit project `Passed!`. If the integration project needs a Postgres it cannot reach, its failures must be the *only* failures and must be pre-existing (compare with Task 0 Step 2); note them in the PR body.

- [ ] **Step 2: Frontend build + existing vitest**

Run: `cd frontend && npm run build 2>&1 | tail -3 && npm test 2>&1 | tail -5; cd ..`
Expected: both succeed.

- [ ] **Step 3: Run the converter against the live tenant filters end to end (API only, no deploy)**

Run:
```bash
dotnet run --project api --no-build -- --help >/dev/null 2>&1 || true
dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~Convert_AllTenantFixtures" 2>&1 | tail -3
```
Expected: `Passed!` (this is the same assertion the deploy verification relies on).

- [ ] **Step 4: Push and open the PR**

```bash
git push -u origin sync-reliability/phase-1
gh pr create --base main --title "Sync reliability Phase 1: DDG filter conversion + failure visibility" --body "$(cat <<'PRBODY'
## Why
All 16 tenant DDGs stopped converting to Graph filters after their recipient filters gained
`RecipientTypeDetails` OR-groups and MailContact/DDL branches (between Jul 22 and Aug 13).
The converter only stripped the single-clause form, reported Success=true with a warning, and the
worker swallowed the Graph 400 per DDG — so the "Avalon Users" list silently shrank from 355 to its
6 explicit emails. Spec: docs/superpowers/specs/2026-08-25-sync-reliability-design.md (Phase 1).

## What
- OPATH tokenizer/parser → fold Exchange-only predicates → simplify → OData render (api/Services/Opath)
- Unknown attributes / unparseable / constant filters ⇒ Success=false
- refresh-ddg returns 422 and keeps the stored filter on failure
- TargetFilterResolver returns per-DDG failures; SyncEngine records them as failed run items and
  counts the tunnel as warned; an empty SpecificUsers scope now targets no mailboxes (was: all)
- Frontend: unconvertible DDGs are unselectable everywhere; no raw-OPATH fallback; Refresh DDG hidden
  for specific-user sources; real error messages in the toast
- Worker: Notes explicitly from CustomAttribute5 (dead aboutMe path removed)

## Tests
- 16 real tenant filters as fixtures (all convert; 9 golden strings)
- parser, folding, like-operators, value-safety, resolver failure reporting, engine run-item test

## Deploy verification
1. `/api/graph/ddgs` → non-zero memberCount for all 16 DDGs
2. Manual run → Avalon tunnel updates ~349 additional mailboxes; run errors list has no DDG failures

🤖 Generated with [Claude Code](https://claude.com/claude-code)
PRBODY
)"
```

- [ ] **Step 5: After merge — deploy and verify on the box (Nick)**

```bash
# on the server, inside tmux
./deploy.sh
# then from anywhere, logged in to the app:
#   GET /api/graph/ddgs  → every memberCount > 0, graphFilterWarning null
#   Trigger a manual sync from the dashboard
#   Runs & Logs → the run's Avalon Gate Code summary shows ~349 updated (September gate code)
```
