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
        if (pattern.Contains('?'))
            throw new NotSupportedException($"wildcard pattern '{pattern}' cannot be expressed as a Graph filter");

        bool leading = pattern.StartsWith('*');
        bool trailing = pattern.Length > 1 && pattern.EndsWith('*');
        var core = pattern;
        if (leading) core = core[1..];
        if (trailing) core = core[..^1];

        // Any '*' remaining after stripping at most one leading and one trailing wildcard is an
        // interior (or extra) wildcard — Graph's startsWith/endsWith/contains only support a
        // wildcard at the edge(s), so this pattern has no faithful OData translation.
        if (core.Contains('*'))
            throw new NotSupportedException($"wildcard pattern '{pattern}' cannot be expressed as a Graph filter");

        var escaped = Escape(core);
        if (leading && trailing) return $"contains({field}, '{escaped}')";
        if (trailing) return $"startsWith({field}, '{escaped}')";
        if (leading) return $"endsWith({field}, '{escaped}')";
        return $"{field} eq '{escaped}'";
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
