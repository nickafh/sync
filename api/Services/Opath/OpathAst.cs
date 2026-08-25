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
