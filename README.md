# AinbFormat

Experimental, typed C# IO for a **subset of TOTK AINB 0x407**.

The library reads and writes decompressed AINB bytes. It does not merge mods,
open ROMFS, unpack SARC, decompress Zstandard, or update resource-size tables.
Those jobs belong to the caller. There is no Python or Rust runtime dependency.

The first package candidate is `0.1.0-alpha.1`. It has been built locally but
has not been published. It is not a complete replacement for a general AINB editor.

## Use

```csharp
using AinbFormat;

IAinbCodec codec = new AinbCodec();
AinbDocument graph = codec.Read(bytes);
byte[] rebuilt = codec.Write(graph);
```

Records and `AinbList<T>` are immutable and own their contents. Node indices must
match array positions. Parameter and connection tables are written grouped by
type, preserving order within each type. Compare per-type sequences rather
than treating the order of different type groups as meaningful.

`Read` throws `AinbUnsupportedException` for recognized content outside the
supported subset. It throws `InvalidDataException` for detected malformed data.
An unsupported file has **not** been completely validated. A caller can preserve
its original bytes, but must not claim it has parsed or merged that file.

## Supported scope

- Commands, GUIDs, primary and secondary command roots.
- Known node kinds and Query/Module/Root flags.
- Typed properties and inputs: int, bool, float, string and vector.
- Null pointer inputs and pointer outputs with class names.
- Direct input sources, multi-source lists, and query-node references.
- Basic Generic/Child/Int/String connections; the two extra words on
  BoolSelector Generic connections.
- Module declarations, empty-blackboard IDs, and an empty optional section 0x6C.

Populated blackboards, EXB expressions, attachments, transitions, XLink actions,
replacement tables, enum-resolution tables, section 0x58, extended parameter
references, pointer properties, advanced selector connections, and unknown
nonzero structural data are rejected. Unsupported node connections are rejected
even if the node type itself is known.

The writer rebuilds offsets, pool strings, node-name hashes and derived counts.
Some editor exports have stale query counts; the reader resolves and validates
queries using the actual Query-flagged nodes, not that advisory count.
Padding, unused strings, and derived metadata are not byte-preserved.
Supported documents are semantically round-trippable, not byte-identical.

## Build and test

Requires the .NET 8 SDK or newer.

```powershell
dotnet build src/AinbFormat
dotnet run --project tests/AinbFormat.Tests
dotnet pack src/AinbFormat -c Release -o artifacts/feed
```

The source-only regression suite includes typed values, references, known hash
vectors, unsupported sections, and every truncated prefix of a synthetic file.
Optional private fixtures compare the native reader against independently
decoded JSON, then verify written files through dt's parser:

```powershell
dotnet run --project tests/AinbFormat.Tests -- path/to/private/fixtures
python tools/verify_reference.py path/to/private/fixtures path/to/dt-ainb
```

The Python command is a development-only check and needs dt's parser dependencies.
Fixtures and game files must not be committed or included in the package.
See [release steps](docs/RELEASING.md) for publishing.

## Credits

Thanks to dt-12345 for [the Python AINB parser](https://github.com/dt-12345/ainb),
and Banan039 / SolidLink95 and the [TotkBits contributors](https://github.com/SolidLink95/TotkBits)
for their format work and independent reference implementations.
ArchLeaders' work on AinbLibrary and TkSharp informed the separation between IO
and merging. The external reference tools are not bundled with this package.

This repository's implementation is licensed under MIT; see [LICENSE](LICENSE).
