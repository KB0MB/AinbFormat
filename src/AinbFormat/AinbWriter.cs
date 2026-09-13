using System.Numerics;
using System.Text;

namespace AinbFormat;

/// <summary>Builds tables first, then patches absolute offsets and appends a shared string pool.</summary>
internal sealed class AinbWriter
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;
    private readonly MemoryStream _strings = new();
    private readonly Dictionary<string, uint> _stringOffsets = new(StringComparer.Ordinal);
    private readonly int[] _header = new int[29];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private AinbWriter() => _writer = new BinaryWriter(_stream, Utf8, leaveOpen: true);

    internal static byte[] Write(AinbDocument document)
    {
        AinbValidation.Validate(document);
        var writer = new AinbWriter();
        try
        {
            return writer.Build(document);
        }
        finally
        {
            writer._writer.Dispose();
            writer._stream.Dispose();
            writer._strings.Dispose();
        }
    }

    private byte[] Build(AinbDocument document)
    {
        var w = _writer;
        var h = _header;
        Reserve(0x74);
        h[0] = 0x20424941;
        h[1] = 0x407;
        h[2] = checked((int)String(document.Name));
        h[3] = document.Commands.Count;
        h[4] = document.Nodes.Count;
        var queryOrdinals = document.Nodes.Where(n => n.Flags.HasFlag(AinbNodeFlags.Query))
            .Select((n, i) => (n.Index, Ordinal: i)).ToDictionary(p => p.Index, p => p.Ordinal);
        h[5] = queryOrdinals.Count;
        h[7] = document.Nodes.Count(n => (int)n.Type >= 200 && (int)n.Type < 300);
        h[24] = checked((int)String(document.Category));
        h[25] = Array.IndexOf(new[] { "AI", "Logic", "Sequence", "UniqueSequence", "UniqueSequenceSPL" }, document.Category);
        foreach (var command in document.Commands)
        {
            w.Write(String(command.Name));
            w.Write(command.Id.ToByteArray());
            w.Write(checked((ushort)command.RootNodeIndex));
            w.Write(checked((ushort)(command.SecondaryRootNodeIndex is int secondary ? secondary + 1 : 0)));
        }
        int nodeHeaders = Position;
        Reserve(document.Nodes.Count * 60);
        h[8] = Position;
        Reserve(48);

        var properties = Enumerable.Range(0, 6).Select(_ => new List<AinbProperty>()).ToArray();
        var inputs = Enumerable.Range(0, 6).Select(_ => new List<AinbInput>()).ToArray();
        var outputs = Enumerable.Range(0, 6).Select(_ => new List<AinbOutput>()).ToArray();
        int queryBase = 0;
        foreach (var node in document.Nodes)
        {
            int body = Position;
            for (int type = 0; type < 6; type++)
            {
                var selected = node.Properties.Where(p => (int)p.Type == type).ToArray();
                w.Write(properties[type].Count);
                w.Write(selected.Length);
                properties[type].AddRange(selected);
            }
            for (int type = 0; type < 6; type++)
            {
                var selectedInputs = node.Inputs.Where(p => (int)p.Type == type).ToArray();
                var selectedOutputs = node.Outputs.Where(p => (int)p.Type == type).ToArray();
                w.Write(inputs[type].Count);
                w.Write(selectedInputs.Length);
                w.Write(outputs[type].Count);
                w.Write(selectedOutputs.Length);
                inputs[type].AddRange(selectedInputs);
                outputs[type].AddRange(selectedOutputs);
            }
            var plugs = node.Plugs.OrderBy(p => PlugGroup(p.Type)).ToArray();
            int first = 0;
            for (int group = 0; group < 10; group++)
            {
                int count = plugs.Count(p => PlugGroup(p.Type) == group);
                w.Write(checked((byte)count));
                w.Write(checked((byte)first));
                first += count;
            }
            int offsets = Position;
            Reserve(plugs.Length * 4);
            for (int i = 0; i < plugs.Length; i++)
            {
                var plug = plugs[i];
                Patch(offsets + i * 4, Position);
                w.Write(plug.NodeIndex);
                w.Write(String(plug.Name));
                if (plug.Unknown1 is uint a)
                {
                    w.Write(a);
                    w.Write(plug.Unknown2!.Value);
                }
            }

            int end = Position;
            _stream.Position = nodeHeaders + node.Index * 60;
            w.Write((ushort)node.Type);
            w.Write(checked((short)node.Index));
            w.Write((ushort)0);
            w.Write((byte)node.Flags);
            w.Write((byte)0);
            w.Write(String(node.Name));
            w.Write(NameHash(node.Name));
            w.Write(0);
            w.Write(body);
            w.Write(0); // No EXB counts or IO-size metadata in the supported subset.
            w.Write(checked((ushort)node.Inputs.Sum(p => p.Sources.Count)));
            w.Write((ushort)0);
            w.Write(0); // Attachment base, with an empty attachment table.
            w.Write(checked((ushort)queryBase));
            w.Write(checked((ushort)node.Queries.Count));
            w.Write(0);
            w.Write(node.Id.ToByteArray());
            queryBase += node.Queries.Count;
            _stream.Position = end;
        }

        h[15] = h[16] = h[11] = Position;
        int propertyTable = Position;
        Reserve(24);
        for (int type = 0; type < 6; type++)
        {
            Patch(propertyTable + type * 4, Position);
            foreach (var property in properties[type])
            {
                w.Write(String(property.Name));
                w.Write(Flags(property.Flags));
                Value(property.Value);
            }
        }

        h[13] = Position;
        int ioTable = Position;
        Reserve(48);
        List<AinbSource> multi = [];
        for (int type = 0; type < 6; type++)
        {
            Patch(ioTable + type * 8, Position);
            foreach (var input in inputs[type])
            {
                w.Write(String(input.Name));
                if (type == 5)
                    w.Write(String(input.ClassName!));
                if (input.Source is { } source)
                {
                    w.Write(checked((short)source.NodeIndex));
                    w.Write((ushort)(source.OutputIndex | (input.IsSetBlackboard == true ? 0x8000 : 0)));
                }
                else
                {
                    w.Write(checked((short)(-100 - multi.Count)));
                    w.Write(checked((short)input.Sources.Count));
                    multi.AddRange(input.Sources);
                }
                w.Write(Flags(input.Flags));
                Value(input.Value);
            }
            Patch(ioTable + type * 8 + 4, Position);
            foreach (var output in outputs[type])
            {
                w.Write(String(output.Name) | (output.IsOutput ? 0x80000000u : 0));
                if (type == 5)
                    w.Write(String(output.ClassName!));
            }
        }
        h[14] = Position;
        foreach (var source in multi)
        {
            w.Write(checked((short)source.NodeIndex));
            w.Write(checked((short)source.OutputIndex));
            w.Write(Flags(source.Flags));
        }
        h[12] = h[19] = h[20] = Position;
        foreach (var node in document.Nodes)
        {
            foreach (int query in node.Queries)
            {
                w.Write(checked((ushort)queryOrdinals[query]));
                w.Write((ushort)0);
            }
        }
        h[23] = Position;
        w.Write(document.Modules.Count);
        foreach (var module in document.Modules)
        {
            w.Write(String(module.Path));
            w.Write(String(module.Category));
            w.Write(module.InstanceCount);
        }
        h[26] = Position;
        w.Write(0);
        h[28] = Position;
        w.Write(document.BlackboardId);
        w.Write(document.ParentBlackboardId);
        h[18] = Position;
        w.Write(0);
        w.Write((short)-1);
        w.Write((short)-1);
        if (document.HasSection6C)
        {
            h[27] = Position;
            w.Write(0);
        }
        h[10] = Position;
        w.Write(0);
        h[9] = Position;
        _strings.WriteTo(_stream);
        for (int i = 0; i < h.Length; i++)
            Patch(i * 4, h[i]);
        return _stream.ToArray();
    }

    internal static int PlugGroup(AinbPlugType type) => type switch
    {
        AinbPlugType.Generic => 0,
        AinbPlugType.Child => 2,
        AinbPlugType.String => 4,
        AinbPlugType.Int => 5,
        _ => throw new InvalidDataException("Unknown connection type.")
    };

    private void Value(AinbValue value)
    {
        switch (value)
        {
            case AinbInt v:
                _writer.Write(v.Value);
                break;
            case AinbBool v:
                _writer.Write(v.Value ? 1u : 0u);
                break;
            case AinbFloat v:
                _writer.Write(v.Value);
                break;
            case AinbString v:
                _writer.Write(String(v.Value));
                break;
            case AinbVector v:
                _writer.Write(v.X);
                _writer.Write(v.Y);
                _writer.Write(v.Z);
                break;
            case AinbNullPointer:
                _writer.Write(0);
                break;
            default:
                throw new InvalidDataException("Unknown value type.");
        }
    }

    private static uint Flags(AinbParameterFlags flags) =>
        (flags.HasFlag(AinbParameterFlags.UsesDefault) ? 0x00800000u : 0) |
        (flags.HasFlag(AinbParameterFlags.IsOutput) ? 0x01000000u : 0);

    private uint String(string value)
    {
        if (_stringOffsets.TryGetValue(value, out uint offset))
            return offset;
        offset = checked((uint)_strings.Length);
        byte[] bytes = Utf8.GetBytes(value);
        _strings.Write(bytes);
        _strings.WriteByte(0);
        _stringOffsets.Add(value, offset);
        return offset;
    }

    // AINB stores MurmurHash3 x86-32 (seed zero) alongside each node name.
    internal static uint NameHash(string name)
    {
        byte[] bytes = Utf8.GetBytes(name);
        uint hash = 0;
        static uint Mix(uint value) => BitOperations.RotateLeft(value * 0xcc9e2d51u, 15) * 0x1b873593u;
        int full = bytes.Length & ~3;
        for (int i = 0; i < full; i += 4)
        {
            hash ^= Mix(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)));
            hash = BitOperations.RotateLeft(hash, 13) * 5 + 0xe6546b64u;
        }
        uint tail = 0;
        for (int i = full; i < bytes.Length; i++)
            tail |= (uint)bytes[i] << ((i - full) * 8);
        if (full != bytes.Length)
            hash ^= Mix(tail);
        hash ^= (uint)bytes.Length;
        hash ^= hash >> 16;
        hash *= 0x85ebca6bu;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35u;
        return hash ^ (hash >> 16);
    }

    private int Position => checked((int)_stream.Position);
    private void Reserve(int bytes) => _writer.Write(new byte[bytes]);
    private void Patch(int offset, int value)
    {
        long end = _stream.Position;
        _stream.Position = offset;
        _writer.Write(value);
        _stream.Position = end;
    }
}
