namespace AinbFormat;

/// <summary>Implemented by external binary IO, never by TkSharp's merge logic.</summary>
public interface IAinbCodec
{
    /// <summary>
    /// Validate and read a decompressed AINB without mutating the input.
    /// Reject recognized unsupported content with AinbUnsupportedException;
    /// detected malformed content throws InvalidDataException. Unsupported input
    /// is not fully validated and must not be treated as a verified graph.
    /// Returned records must own their data after this call returns.
    /// </summary>
    AinbDocument Read(ReadOnlySpan<byte> data);

    /// <summary>
    /// Write a supported document to owned, decompressed AINB bytes. Preserve
    /// all represented fields and reject unsupported features. SARC, Zstandard,
    /// ROMFS lookup and resource-size tables are the caller's responsibility.
    /// </summary>
    byte[] Write(AinbDocument document);
}
