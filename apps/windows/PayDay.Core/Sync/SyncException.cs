namespace PayDay.Sync;

/// <summary>
/// A sync operation failed with one of the error codes in <c>spec/sync-protocol.md</c> section 14.
/// The code is part of the cross-platform contract: every app fails with the same code for the
/// same input, and the conformance fixtures check it.
/// </summary>
public sealed class SyncException : Exception
{
    public SyncException(string code)
        : base(code)
    {
        Code = code;
    }

    public SyncException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public SyncException(string code, string message, Exception innerException)
        : base($"{code}: {message}", innerException)
    {
        Code = code;
    }

    /// <summary>The camelCase error code, for example <c>authFailed</c>.</summary>
    public string Code { get; }
}

/// <summary>Error codes from <c>spec/sync-protocol.md</c> section 14.</summary>
public static class SyncErrorCodes
{
    public const string Malformed = "malformed";
    public const string WallOutOfRange = "wallOutOfRange";
    public const string CounterOutOfRange = "counterOutOfRange";
    public const string BadDeviceId = "badDeviceId";
    public const string BadInput = "badInput";
    public const string NotAUuid = "notAUuid";
    public const string NotLegacy = "notLegacy";
    public const string TooShort = "tooShort";
    public const string BadMagic = "badMagic";
    public const string UnsupportedVersion = "unsupportedVersion";
    public const string UnknownKind = "unknownKind";
    public const string UnsupportedCompression = "unsupportedCompression";
    public const string ReservedNonZero = "reservedNonZero";
    public const string KindMismatch = "kindMismatch";
    public const string BadKeyGeneration = "badKeyGeneration";
    public const string UnknownKeyGeneration = "unknownKeyGeneration";
    public const string AuthFailed = "authFailed";
    public const string UnsupportedFormat = "unsupportedFormat";
    public const string TypeMismatch = "typeMismatch";
    public const string VaultMismatch = "vaultMismatch";
    public const string DeviceMismatch = "deviceMismatch";
    public const string SeqMismatch = "seqMismatch";
    public const string HlcMismatch = "hlcMismatch";
    public const string BadPath = "badPath";
    public const string BadSeq = "badSeq";
    public const string WrongWordCount = "wrongWordCount";
    public const string UnknownWord = "unknownWord";
    public const string BadChecksum = "badChecksum";
    public const string BadQr = "badQr";
    public const string BadKeyLength = "badKeyLength";
    public const string BadNonceLength = "badNonceLength";
}
