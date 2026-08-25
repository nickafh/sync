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
