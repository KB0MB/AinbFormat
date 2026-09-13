using System.Buffers.Binary;
using System.Text;

namespace AinbFormat;

/// <summary>Offset-based reader; every slice is checked before accessing the input.</summary>
internal sealed class AinbReader
{
    private readonly byte[] _data;
    private readonly bool[] _read;
    private readonly int[] _header;
    private readonly int _pool;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private AinbReader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x74 || data.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("AINB size is outside the supported range.");
        _data = data.ToArray();
        _read = new bool[data.Length];
        if (!data[..4].SequenceEqual("AIB "u8))
            throw new InvalidDataException("Invalid AINB magic.");
        _header = Enumerable.Range(0, 29).Select(i => checked((int)U32(i * 4))).ToArray();
        if (_header[1] != 0x407)
            throw new AinbUnsupportedException($"Unsupported AINB version 0x{_header[1]:x}.");
        _pool = _header[9];
        Require(_pool >= 0x74 && _pool < _data.Length, "Invalid string pool offset.");
        foreach (int index in new[] { 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 22, 23, 26, 27, 28 })
            if (_header[index] != 0)
                Range(_header[index], 0);
        Require(_header[3] <= 65535 && _header[4] <= 32767, "Excessive command or node count.");
        Range(0x74, checked(_header[3] * 24 + _header[4] * 60));
        Require(_header[8] >= 0x74 + _header[3] * 24 + _header[4] * 60,
            "Blackboard overlaps node headers.");
        foreach (int index in new[] { 8, 10, 11, 12, 13, 14, 15, 16, 18, 19, 20, 23, 26, 28 })
            Require(_header[index] >= _header[8] && _header[index] < _pool, "Section is outside structural data.");
    }

    internal static AinbDocument Read(ReadOnlySpan<byte> data)
    {
        try
        {
            return new AinbReader(data).ReadDocument();
        }
        catch (OverflowException error) { throw new InvalidDataException("AINB offset/count overflow.", error); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid AINB UTF-8 string.", error); }
    }

    private AinbDocument ReadDocument()
    {
        var h = _header;
        // These sections cannot be represented by the initial package. Reject before
        // constructing a graph that a caller might mistake for a lossless parse.
        Range(h[8], 48);
        for (int type = 0; type < 6; type++)
        {
            Unsupported(U16(h[8] + type * 8) != 0, "Blackboard parameters");
            Unsupported(U16(h[8] + type * 8 + 6) != 0, "Blackboard header padding");
            // Empty-table base/value offsets are derived metadata, not parameters.
            _ = U32(h[8] + type * 8 + 2);
        }
        Unsupported(h[6] != 0 || h[15] != h[16], "Attachments");
        Unsupported(h[16] != h[11], "Attachment index table");
        Unsupported(h[17] != 0, "EXB expressions");
        Unsupported(h[22] != 0, "Section 0x58");
        Unsupported(h[21] != 0, "Header section 0x54");
        Unsupported(h[12] != h[19], "Transitions");
        Unsupported(h[20] != h[12], "Section 0x50");
        Unsupported(U32(h[26]) != 0, "XLink actions");
        Unsupported(U32(h[10]) != 0, "Enum resolutions");
        if (h[27] != 0)
            Unsupported(U32(h[27]) != 0, "Section 0x6C contents");
        Range(h[18], 8);
        Unsupported(U32(h[18]) != 0, "Replacement table");
        Unsupported(I16(h[18] + 4) != -1 || I16(h[18] + 6) != -1, "Replacement header");

        var properties = ReadProperties();
        var multi = ReadMultiSources();
        var (inputs, outputs) = ReadParameters(multi);
        var commands = new List<AinbCommand>();
        for (int i = 0; i < h[3]; i++)
        {
            int p = 0x74 + i * 24;
            int secondary = U16(p + 22);
            commands.Add(new(StringAt(p), new Guid(Bytes(p + 4, 16)), U16(p + 20),
                secondary == 0 ? null : secondary - 1));
        }

        var modules = new List<AinbModule>();
        int count = Count(h[23]);
        Range(h[23] + 4, checked(count * 12));
        for (int i = 0; i < count; i++)
        {
            int p = h[23] + 4 + i * 12;
            modules.Add(new(StringAt(p), StringAt(p + 4), U32(p + 8)));
        }

        int nodeStart = 0x74 + h[3] * 24;
        var queryNodes = Enumerable.Range(0, h[4])
            .Where(i => (_data[nodeStart + i * 60 + 6] & 1) != 0).ToArray();
        // Some editor exports leave the advisory header count stale. Query references
        // use the flagged-node table, so validate those references and rebuild the count.
        Require(h[23] >= h[19] && (h[23] - h[19]) % 4 == 0, "Invalid query table.");
        int queryCount = (h[23] - h[19]) / 4;
        var nodes = new List<AinbNode>();
        for (int index = 0; index < h[4]; index++)
        {
            int p = nodeStart + index * 60;
            var type = (AinbNodeType)U16(p);
            Unsupported(!Enum.IsDefined(type), "Node type");
            Require(I16(p + 2) == index, "Node index disagrees with its array position.");
            Unsupported(U16(p + 4) != 0, "Node attachments");
            byte flags = _data[p + 6];
            _ = Bytes(p + 6, 2);
            Unsupported((flags & ~7) != 0, "Node flags");
            Unsupported(_data[p + 7] != 0 || U32(p + 16) != 0 || U32(p + 40) != 0,
                "Unknown node-header data");
            Unsupported(U32(p + 24) != 0 || U16(p + 30) != 0, "Node expression metadata");
            string name = StringAt(p + 8);
            // Name hashes and per-node multi-source counts are rebuilt by the writer.
            _ = U32(p + 12);
            _ = U16(p + 28);
            _ = U32(p + 32);
            int body = Offset(p + 20);
            Require(body >= h[8] + 48 && body <= h[15] - 164, "Node body is outside its section.");
            Range(body, 164);
            var nodeProperties = new List<AinbProperty>();
            var nodeInputs = new List<AinbInput>();
            var nodeOutputs = new List<AinbOutput>();
            for (int t = 0; t < 6; t++)
            {
                nodeProperties.AddRange(Selection(properties[t], body + t * 8));
                nodeInputs.AddRange(Selection(inputs[t], body + 48 + t * 16));
                nodeOutputs.AddRange(Selection(outputs[t], body + 56 + t * 16));
            }
            int firstQuery = U16(p + 36), queries = U16(p + 38);
            Require(firstQuery <= queryCount && queries <= queryCount - firstQuery, "Node query slice is invalid.");
            var resolvedQueries = new List<int>();
            for (int q = 0; q < queries; q++)
            {
                int offset = h[19] + (firstQuery + q) * 4;
                int query = U16(offset);
                Require(query < queryNodes.Length && U16(offset + 2) == 0, "Invalid query-node reference.");
                resolvedQueries.Add(queryNodes[query]);
            }

            var plugs = new List<AinbPlug>();
            _ = Bytes(body + 144, 20);
            for (int group = 0; group < 10; group++)
            {
                int plugCount = _data[body + 144 + group * 2];
                int first = _data[body + 145 + group * 2];
                Unsupported(plugCount != 0 && group is not (0 or 2 or 4 or 5), "Connection type");
                Range(body + 164 + first * 4, plugCount * 4);
                for (int j = 0; j < plugCount; j++)
                {
                    int plug = Offset(body + 164 + (first + j) * 4);
                    Require(plug >= body + 164 && plug <= h[15] - 8, "Connection record is outside node data.");
                    Unsupported(
                        group == 2 && (type is AinbNodeType.S32Selector or AinbNodeType.F32Selector
                            or AinbNodeType.StringSelector or AinbNodeType.RandomSelector ||
                            name is "SelectorBSABrainVerbUpdater" or "SelectorBSAFormChangeUpdater") ||
                        group == 0 && type is AinbNodeType.F32Selector or AinbNodeType.Expression ||
                        group == 4 && type is AinbNodeType.StringSelector or AinbNodeType.Expression ||
                        group == 5 && type is AinbNodeType.S32Selector or AinbNodeType.Expression,
                        "Advanced selector connection");
                    uint? a = null, b = null;
                    if (group == 0 && type == AinbNodeType.BoolSelector)
                    {
                        a = U32(plug + 8);
                        b = U32(plug + 12);
                    }
                    plugs.Add(new(group switch
                    {
                        0 => AinbPlugType.Generic,
                        2 => AinbPlugType.Child,
                        4 => AinbPlugType.String,
                        _ => AinbPlugType.Int
                    }, I32(plug), StringAt(plug + 4), a, b));
                }
            }
            nodes.Add(new()
            {
                Index = index,
                Type = type,
                Name = name,
                Id = new Guid(Bytes(p + 44, 16)),
                Flags = (AinbNodeFlags)flags,
                Queries = resolvedQueries.ToAinbList(),
                Properties = nodeProperties.ToAinbList(),
                Inputs = nodeInputs.ToAinbList(),
                Outputs = nodeOutputs.ToAinbList(),
                Plugs = plugs.ToAinbList()
            });
        }

        string category = String(_header[24]);
        string[] categories = ["AI", "Logic", "Sequence", "UniqueSequence", "UniqueSequenceSPL"];
        Require(h[25] < categories.Length && category == categories[h[25]], "Category fields disagree.");
        var result = new AinbDocument
        {
            Name = String(h[2]),
            Category = category,
            BlackboardId = U32(h[28]),
            ParentBlackboardId = U32(h[28] + 4),
            HasSection6C = h[27] != 0,
            Commands = commands.ToAinbList(),
            Nodes = nodes.ToAinbList(),
            Modules = modules.ToAinbList()
        };
        AinbValidation.Validate(result);
        // A successful parse must not discard unrepresented nonzero structural data.
        // Zero padding and unreferenced strings are safe to omit when rebuilding tables.
        for (int offset = 0; offset < _pool; offset++)
            Unsupported(!_read[offset] && _data[offset] != 0, $"Unrepresented data at 0x{offset:x}");
        return result;
    }

    private List<AinbProperty>[] ReadProperties()
    {
        var result = new List<AinbProperty>[6];
        int start = _header[11];
        Range(start, 24);
        for (int type = 0; type < 6; type++)
        {
            int p = Offset(start + type * 4);
            Require(p >= start + 24, "Property data overlaps its offset table.");
            int end = type == 5 ? _header[13] : Offset(start + (type + 1) * 4);
            int size = type == 4 ? 20 : 12;
            Table(p, end, size);
            Unsupported(type == 5 && p != end, "Pointer properties with class names");
            result[type] = [];
            for (; p < end; p += size)
                result[type].Add(new((AinbDataType)type, StringAt(p), Value(type, p + 8),
                    Flags(U32(p + 4))));
        }
        return result;
    }

    private List<AinbSource> ReadMultiSources()
    {
        int start = _header[14], end = _header[12];
        Table(start, end, 8);
        var result = new List<AinbSource>();
        for (int p = start; p < end; p += 8)
            result.Add(new(I16(p), I16(p + 2), Flags(U32(p + 4))));
        return result;
    }

    private (List<AinbInput>[], List<AinbOutput>[]) ReadParameters(List<AinbSource> multi)
    {
        var inputs = new List<AinbInput>[6];
        var outputs = new List<AinbOutput>[6];
        int table = _header[13];
        Range(table, 48);
        for (int type = 0; type < 6; type++)
        {
            int start = Offset(table + type * 8), middle = Offset(table + type * 8 + 4);
            Require(start >= table + 48, "Parameter data overlaps its offset table.");
            int end = type == 5 ? _header[14] : Offset(table + (type + 1) * 8);
            int inputSize = type == 4 ? 24 : type == 5 ? 20 : 16;
            Table(start, middle, inputSize);
            Table(middle, end, type == 5 ? 8 : 4);
            inputs[type] = [];
            outputs[type] = [];
            for (int p = start; p < middle; p += inputSize)
            {
                int sourceOffset = p + (type == 5 ? 8 : 4);
                int node = I16(sourceOffset), output = I16(sourceOffset + 2);
                var flags = Flags(U32(sourceOffset + 4));
                bool? setBlackboard = null;
                AinbSource? source = null;
                List<AinbSource> sources = [];
                if (node <= -100)
                {
                    int first = -100 - node;
                    Require(output > 0 && first <= multi.Count && output <= multi.Count - first,
                        "Invalid multi-source slice.");
                    sources.AddRange(multi.GetRange(first, output));
                }
                else
                {
                    if (output < 0)
                    {
                        setBlackboard = true;
                        output &= 0x7fff;
                    }
                    source = new(node, output);
                }
                inputs[type].Add(new((AinbDataType)type, StringAt(p), Value(type, sourceOffset + 8),
                    flags, type == 5 ? StringAt(p + 4) : null, setBlackboard, source, sources.ToAinbList()));
            }
            for (int p = middle; p < end; p += type == 5 ? 8 : 4)
            {
                uint bits = U32(p);
                Unsupported((bits & 0x40000000) != 0, "Unknown output flag");
                outputs[type].Add(new((AinbDataType)type, String(checked((int)(bits & 0x3fffffff))),
                    (bits & 0x80000000) != 0, type == 5 ? StringAt(p + 4) : null));
            }
        }
        return (inputs, outputs);
    }

    private AinbValue Value(int type, int p) => type switch
    {
        0 => new AinbInt(I32(p)),
        1 when U32(p) <= 1 => new AinbBool(U32(p) != 0),
        2 => new AinbFloat(F32(p)),
        3 => new AinbString(StringAt(p)),
        4 => new AinbVector(F32(p), F32(p + 4), F32(p + 8)),
        5 when U32(p) == 0 => new AinbNullPointer(),
        _ => throw new AinbUnsupportedException("Noncanonical boolean or non-null pointer value.")
    };

    private static AinbParameterFlags Flags(uint bits)
    {
        Unsupported((bits & ~0x01800000u) != 0, "Extended parameter reference/flags");
        return ((bits & 0x00800000) != 0 ? AinbParameterFlags.UsesDefault : 0) |
            ((bits & 0x01000000) != 0 ? AinbParameterFlags.IsOutput : 0);
    }

    private IEnumerable<T> Selection<T>(List<T> list, int p)
    {
        int first = Count(p), count = Count(p + 4);
        Require(first <= list.Count && count <= list.Count - first, "Invalid node parameter slice.");
        return list.GetRange(first, count);
    }

    private void Table(int start, int end, int stride)
    {
        Require(end >= start && (end - start) % stride == 0, "Invalid typed table bounds.");
        Require(end <= _pool, "Typed table overlaps the string pool.");
        Range(start, end - start);
    }

    private int Count(int p) => checked((int)U32(p));
    private int Offset(int p) => checked((int)U32(p));
    private uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(p, 4));
    private int I32(int p) => unchecked((int)U32(p));
    private ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(p, 2));
    private short I16(int p) => unchecked((short)U16(p));
    private float F32(int p) => BitConverter.Int32BitsToSingle(I32(p));
    private string StringAt(int p) => String(Offset(p));
    private string String(int relative)
    {
        int start = checked(_pool + relative);
        Require(start >= _pool && start < _data.Length, "String reference is outside the pool.");
        int end = Array.IndexOf(_data, (byte)0, start);
        Require(end >= start, "Unterminated AINB string.");
        return Utf8.GetString(_data, start, end - start);
    }
    private ReadOnlySpan<byte> Bytes(int start, int length)
    {
        Range(start, length);
        Array.Fill(_read, true, start, length);
        return _data.AsSpan(start, length);
    }
    private void Range(int start, int length) =>
        Require(start >= 0 && length >= 0 && start <= _data.Length && length <= _data.Length - start,
            "AINB offset or count exceeds the input.");
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
    private static void Unsupported(bool condition, string feature)
    {
        if (condition)
            throw new AinbUnsupportedException(feature + " is not supported yet.");
    }
}
