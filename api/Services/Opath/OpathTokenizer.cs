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
