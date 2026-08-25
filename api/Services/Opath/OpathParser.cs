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
