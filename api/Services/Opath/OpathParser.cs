namespace AFHSync.Api.Services.Opath;

/// <summary>
/// Recursive-descent parser for Exchange OPATH recipient filters.
/// Grammar (precedence low → high):  or := and ('-or' and)*  |  and := not ('-and' not)*  |
/// not := '-not' not | primary  |  primary := '(' or ')' | $bool | Identifier Operator Value
/// </summary>
public sealed class OpathParser
{
    /// <summary>
    /// Max nesting depth for parenthesized groups and '-not' chains. A StackOverflowException
    /// is uncatchable and kills the process (API and worker alike); this cap turns a
    /// pathologically deep filter into a normal, catchable OpathParseException well before the
    /// recursive-descent call stack gets anywhere near the runtime's limit. Bounding parser
    /// recursion here also bounds every downstream recursion (Fold, Simplify, both renderers),
    /// since none of them can produce a tree deeper than what the parser itself accepted.
    /// </summary>
    private const int MaxNestingDepth = 200;

    private readonly List<OpathToken> _tokens;
    private int _pos;
    private int _depth;

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

    private void EnterNesting(int position)
    {
        _depth++;
        if (_depth > MaxNestingDepth)
            throw new OpathParseException("Filter nests too deeply", position);
    }

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
            var token = Advance();
            EnterNesting(token.Position);
            try
            {
                return new OpathNot(ParseNot());
            }
            finally
            {
                _depth--;
            }
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
                EnterNesting(token.Position);
                try
                {
                    var inner = ParseOr();
                    Expect(OpathTokenKind.RParen);
                    return inner;
                }
                finally
                {
                    _depth--;
                }
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
