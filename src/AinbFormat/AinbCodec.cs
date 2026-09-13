namespace AinbFormat;

/// <summary>Reads and writes decompressed TOTK AINB 0x407 graphs.</summary>
public sealed class AinbCodec : IAinbCodec
{
    public AinbDocument Read(ReadOnlySpan<byte> data) => AinbReader.Read(data);
    public byte[] Write(AinbDocument document) => AinbWriter.Write(document);
}

/// <summary>A recognized AINB feature is not represented by this package.</summary>
public sealed class AinbUnsupportedException(string message) : NotSupportedException(message);
