namespace AinbFormat;

/// <summary>Checks graph references and the limits of the supported binary representation.</summary>
public static class AinbValidation
{
    public static void Validate(AinbDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != 0x407 || document.UnsupportedFeatures != AinbUnsupportedFeatures.None)
            throw new AinbUnsupportedException("Only the supported AINB 0x407 subset can be written.");
        Require(document.Nodes.Count <= 32767 && document.Commands.Count <= 65535, "Too many nodes or commands.");
        Require(new[] { "AI", "Logic", "Sequence", "UniqueSequence", "UniqueSequenceSPL" }.Contains(document.Category),
            "Unknown category.");
        Text(document.Name);
        Text(document.Category);
        void Reference(int index, bool optional = false)
        {
            if (optional && index is -1 or 32767)
                return;
            Require((uint)index < document.Nodes.Count, "Node reference is out of range.");
        }
        HashSet<string> commands = new(StringComparer.Ordinal);
        foreach (var command in document.Commands)
        {
            Text(command.Name);
            Require(commands.Add(command.Name), "Duplicate command name.");
            Reference(command.RootNodeIndex);
            if (command.SecondaryRootNodeIndex is int secondary)
                Reference(secondary);
        }
        foreach (var module in document.Modules)
        {
            Text(module.Path);
            Text(module.Category);
        }
        int multiCount = 0, queryCount = 0;
        for (int nodeIndex = 0; nodeIndex < document.Nodes.Count; nodeIndex++)
        {
            var node = document.Nodes[nodeIndex];
            Require(node.Index == nodeIndex, "Node index disagrees with array order.");
            Require(Enum.IsDefined(node.Type) && (node.Flags & ~(AinbNodeFlags)7) == 0, "Unknown node type or flags.");
            Text(node.Name);
            queryCount = checked(queryCount + node.Queries.Count);
            Require(queryCount <= 65535, "Query table exceeds 16-bit indexing.");
            foreach (int query in node.Queries)
            {
                Reference(query);
                Require(document.Nodes[query].Flags.HasFlag(AinbNodeFlags.Query), "Query target is not a query node.");
            }
            foreach (var property in node.Properties)
            {
                Text(property.Name);
                Flags(property.Flags);
                Value(property.Type, property.Value);
                if (property.Type == AinbDataType.Pointer)
                    throw new AinbUnsupportedException("Pointer properties need a class-name representation.");
            }
            foreach (var output in node.Outputs)
            {
                Require(Enum.IsDefined(output.Type), "Unknown output type.");
                Text(output.Name);
                ClassName(output.Type, output.ClassName);
            }
            foreach (var input in node.Inputs)
            {
                Text(input.Name);
                Value(input.Type, input.Value);
                Flags(input.Flags);
                ClassName(input.Type, input.ClassName);
                Require((input.Source is null) != (input.Sources.Count == 0), "Expected a direct source or a nonempty source list.");
                Require(input.IsSetBlackboard is not false, "Use null for an unset blackboard bit.");
                if (input.Source is { } direct)
                    Require(direct.Flags == AinbParameterFlags.None, "Direct-source flags belong on the input.");
                else
                {
                    Require(input.IsSetBlackboard is null, "Multi-source inputs cannot carry the direct-source blackboard bit.");
                    Require(input.Sources.Count <= 32767, "Too many sources for one input.");
                    multiCount = checked(multiCount + input.Sources.Count);
                    Require(multiCount <= 32669, "Multi-source table exceeds signed-index encoding.");
                }
                foreach (var source in input.Source is { } single ? new[] { single } : input.Sources.ToArray())
                {
                    Reference(source.NodeIndex, true);
                    Flags(source.Flags);
                    Require(source.OutputIndex >= 0 && source.OutputIndex <= 32767, "Output index is not representable.");
                    if (source.NodeIndex is -1 or 32767)
                        continue;
                    int count = document.Nodes[source.NodeIndex].Outputs.Count(p => p.Type == input.Type);
                    Require(source.OutputIndex < count, "Source output index is out of range for its type.");
                }
            }
            Require(node.Plugs.Count <= 255, "Too many connections for an 8-bit connection table.");
            foreach (var plug in node.Plugs)
            {
                Reference(plug.NodeIndex, true);
                Text(plug.Name);
                Require(Enum.IsDefined(plug.Type), "Unknown connection type.");
                bool advanced =
                    plug.Type == AinbPlugType.Child && (node.Type is AinbNodeType.S32Selector or AinbNodeType.F32Selector
                        or AinbNodeType.StringSelector or AinbNodeType.RandomSelector ||
                        node.Name is "SelectorBSABrainVerbUpdater" or "SelectorBSAFormChangeUpdater") ||
                    plug.Type == AinbPlugType.Generic && node.Type is AinbNodeType.F32Selector or AinbNodeType.Expression ||
                    plug.Type == AinbPlugType.String && node.Type is AinbNodeType.StringSelector or AinbNodeType.Expression ||
                    plug.Type == AinbPlugType.Int && node.Type is AinbNodeType.S32Selector or AinbNodeType.Expression;
                if (advanced)
                    throw new AinbUnsupportedException("Advanced selector connections are not represented.");
                bool extra = plug.Type == AinbPlugType.Generic && node.Type == AinbNodeType.BoolSelector;
                Require(extra ? plug.Unknown1.HasValue && plug.Unknown2.HasValue :
                    plug.Unknown1 is null && plug.Unknown2 is null, "Connection metadata does not match its node type.");
            }
        }
    }

    private static void ClassName(AinbDataType type, string? name)
    {
        Require(type == AinbDataType.Pointer ? name is not null : name is null, "Unexpected or missing pointer class name.");
        if (name is not null)
            Text(name);
    }

    private static void Flags(AinbParameterFlags flags) =>
        Require((flags & ~(AinbParameterFlags)3) == 0, "Unknown parameter flags.");

    private static void Value(AinbDataType type, AinbValue value)
    {
        bool valid = (type, value) switch
        {
            (AinbDataType.Int, AinbInt) or (AinbDataType.Bool, AinbBool) or
            (AinbDataType.Pointer, AinbNullPointer) => true,
            (AinbDataType.String, AinbString s) => s.Value is not null && !s.Value.Contains('\0'),
            (AinbDataType.Float, AinbFloat f) => float.IsFinite(f.Value),
            (AinbDataType.Vector3F, AinbVector v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z),
            _ => false
        };
        Require(valid, "Value does not match its parameter type or is not finite.");
    }

    private static void Text(string value) => Require(value is not null && !value.Contains('\0'), "Invalid string.");
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
