using System;

namespace Oppaku.Core.Exceptions;

public enum ErrorCode
{
    InvalidChunk,
    MetadataCorrupt,
    SparseFailed,
    ChecksumMismatch
}

public class OppakuException : Exception
{
    public ErrorCode Code { get; }

    public OppakuException(ErrorCode code, string message) : base(message)
    {
        Code = code;
    }

    public OppakuException(ErrorCode code, string message, Exception innerException) : base(message, innerException)
    {
        Code = code;
    }
}
