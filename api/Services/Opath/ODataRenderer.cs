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
