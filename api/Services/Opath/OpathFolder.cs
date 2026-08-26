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

        // A bare '*' matches every value regardless of attribute — fold it to a constant so it
        // never reaches ODataRenderer.RenderLike, which cannot express "match everything" as a
        // Graph function (and previously rendered it as the nonsensical endsWith(f,'')).
        if (c.Operator is "like" or "notlike" && c.Value == "*")
            return new OpathConst(c.Operator == "like");

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
