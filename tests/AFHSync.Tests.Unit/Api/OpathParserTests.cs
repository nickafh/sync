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

    // A StackOverflowException is uncatchable and kills the process; the parser must reject
    // filters that nest beyond a sane depth before recursion ever gets that far. 201 parenthesis
    // levels (one past the 200 cap) around an otherwise-valid comparison must throw a normal,
    // catchable OpathParseException rather than recursing further.
    [Fact]
    public void Parse_ExceedsMaxNestingDepth_Throws()
    {
        var input = new string('(', 201) + "Office -eq 'Buckhead'" + new string(')', 201);

        var ex = Assert.Throws<OpathParseException>(() => OpathParser.Parse(input));
        Assert.Contains("nests too deeply", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Exactly at the cap must still parse successfully — the guard should reject depth > 200,
    // not depth == 200.
    [Fact]
    public void Parse_AtMaxNestingDepth_Succeeds()
    {
        var input = new string('(', 200) + "Office -eq 'Buckhead'" + new string(')', 200);

        var node = OpathParser.Parse(input);

        Assert.IsType<OpathCompare>(node);
    }
}
