using System.Buffers.Binary;
using AinbFormat;

namespace AinbFormat.Tests;

internal static class Regression
{
    internal static int Run(AinbCodec codec)
    {
        int passed = 0;
        void Check(string name, Action test)
        {
            test();
            Console.WriteLine("PASS " + name);
            passed++;
        }
        void Reject<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        var document = Example();
        byte[] raw = codec.Write(document);
        Check("synthetic typed values, queries, sources, GUIDs, connections and modules", () =>
        {
            if (codec.Read(raw) != document)
                throw new Exception("Synthetic round trip differs.");
            if (!codec.Write(document).SequenceEqual(raw))
                throw new Exception("Writer is nondeterministic.");
        });
        Check("known MurmurHash3 name vector", () =>
        {
            int nodeHeader = 0x74 + document.Commands.Count * 24;
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(nodeHeader + 12)) != 0x248bfa47)
                throw new Exception("Name hash differs from MurmurHash3('hello', seed 0).");
        });
        Check("reader owns its data", () =>
        {
            byte[] input = (byte[])raw.Clone();
            var parsed = codec.Read(input);
            Array.Clear(input);
            if (parsed != document)
                throw new Exception("Reader retained mutable input memory.");
        });
        Check("every truncated prefix is rejected", () =>
        {
            for (int length = 0; length < raw.Length; length++)
                Reject<InvalidDataException>(() => codec.Read(raw.AsSpan(0, length)));
        });
        byte[] Changed(int offset, uint value)
        {
            var changed = (byte[])raw.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(offset), value);
            return changed;
        }
        int Field(int index) => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(index * 4));
        Check("bad magic", () => Reject<InvalidDataException>(() => codec.Read(Changed(0, 0))));
        Check("overflowing offset", () => Reject<InvalidDataException>(() => codec.Read(Changed(9 * 4, uint.MaxValue))));
        Check("unsupported version", () => Reject<AinbUnsupportedException>(() => codec.Read(Changed(4, 0x404))));
        Check("populated blackboard rejected", () => Reject<AinbUnsupportedException>(() => codec.Read(Changed(Field(8), 1))));
        Check("EXB rejected", () => Reject<AinbUnsupportedException>(() => codec.Read(Changed(17 * 4, (uint)Field(8)))));
        Check("replacement metadata rejected", () => Reject<AinbUnsupportedException>(() => codec.Read(Changed(Field(18), 1))));
        Check("unknown section contents rejected", () => Reject<AinbUnsupportedException>(() => codec.Read(Changed(Field(27), 1))));
        Check("node index validated", () =>
        {
            var bad = document with
            {
                Nodes = document.Nodes.Select((n, i) => i == 1 ? n with { Index = 0 } : n).ToAinbList()
            };
            Reject<InvalidDataException>(() => codec.Write(bad));
        });
        Check("value type validated", () =>
        {
            var badNode = document.Nodes[0] with
            {
                Properties = new([new(AinbDataType.Bool, "Bad", new AinbInt(1))])
            };
            Reject<InvalidDataException>(() => codec.Write(document with { Nodes = new([badNode, .. document.Nodes.Skip(1)]) }));
        });
        Check("dangling input output index validated", () =>
        {
            var badNode = document.Nodes[0] with
            {
                Inputs = new([new(AinbDataType.Int, "Bad", new AinbInt(0), 0, null, null, new(2, 7), new())])
            };
            Reject<InvalidDataException>(() => codec.Write(document with { Nodes = new([badNode, .. document.Nodes.Skip(1)]) }));
        });
        Check("advanced selector metadata rejected", () =>
        {
            var badNode = document.Nodes[0] with
            {
                Type = AinbNodeType.F32Selector
            };
            Reject<AinbUnsupportedException>(() => codec.Write(document with { Nodes = new([badNode, .. document.Nodes.Skip(1)]) }));
        });
        Check("embedded string terminator rejected", () =>
            Reject<InvalidDataException>(() => codec.Write(document with { Name = "bad\0name" })));
        Check("stale derived query count rebuilt", () =>
        {
            var parsed = codec.Read(Changed(5 * 4, 900));
            if (parsed != document)
                throw new Exception("Derived count changed graph semantics.");
        });
        Check("section 6C absent", () =>
        {
            var without = document with
            {
                HasSection6C = false
            };
            if (codec.Read(codec.Write(without)) != without)
                throw new Exception("Optional section mismatch.");
        });
        return passed;
    }

    private static AinbDocument Example()
    {
        AinbValue[] values = [new AinbInt(-34), new AinbBool(true), new AinbFloat(1.25f),
            new AinbString("\u96ea"), new AinbVector(1, -2, 3), new AinbNullPointer()];
        var root = new AinbNode
        {
            Index = 0,
            Id = Guid.Parse("12345678-1234-5678-9abc-123456789abc"),
            Name = "hello",
            Type = AinbNodeType.BoolSelector,
            Flags = AinbNodeFlags.Root,
            Queries = new([2, 1]),
            Properties = values.Take(5).Select((v, i) => new AinbProperty((AinbDataType)i, "Property" + i, v,
                AinbParameterFlags.UsesDefault)).ToAinbList(),
            Inputs = values.Select((v, i) => new AinbInput((AinbDataType)i, "Input" + i, v,
                AinbParameterFlags.IsOutput, i == 5 ? "ExampleClass" : null, i == 1 ? true : null,
                i == 2 ? null : new(2, 0), i == 2 ? new([new(2, 0, AinbParameterFlags.IsOutput), new(-1, 0)]) : new())).ToAinbList(),
            Plugs = new([new(AinbPlugType.Generic, 1, "True", 1, 2),
                new(AinbPlugType.Child, 2, ""), new(AinbPlugType.String, -1, "Missing"),
                new(AinbPlugType.Int, 32767, "Unset")])
        };
        return new()
        {
            Name = "Synthetic.module",
            Category = "AI",
            BlackboardId = 0x12345678,
            ParentBlackboardId = uint.MaxValue,
            HasSection6C = true,
            Commands = new([new("Root", Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 0, 2)]),
            Nodes = new([root, new() { Index = 1, Name = "foo", Flags = AinbNodeFlags.Query },
                new() { Index = 2, Name = "Producer", Flags = AinbNodeFlags.Query,
                    Outputs = values.Select((v, i) => new AinbOutput((AinbDataType)i, "Output" + i, true,
                        i == 5 ? "ExampleClass" : null)).ToAinbList() }]),
            Modules = new([new("Example.module.ainb", "AI", 2)])
        };
    }
}
