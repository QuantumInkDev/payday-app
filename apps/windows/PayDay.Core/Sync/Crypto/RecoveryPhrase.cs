using System.Security.Cryptography;
using System.Text;

namespace PayDay.Sync.Crypto;

/// <summary>
/// The recovery phrase (<c>spec/sync-protocol.md</c> section 8): the 32-byte vault key as 24
/// BIP39 English words (entropy plus an 8-bit SHA-256 checksum). The BIP39 seed derivation is not used.
/// The wordlist is <c>spec/bip39-english.txt</c>, embedded unchanged and checked against its hash.
/// </summary>
public static class RecoveryPhrase
{
    public const int WordCount = 24;
    public const string WordlistSha256 = "2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda";
    public const string WordlistResourceName = "PayDay.Sync.Crypto.bip39-english.txt";

    /// <summary>The only separators between words: space, tab, CR, LF and no-break space.</summary>
    private const string Separators = " \t\r\n ";

    private static readonly Lazy<Wordlist> List = new(LoadWordlist);

    /// <summary>The 2048 BIP39 English words in list order.</summary>
    public static IReadOnlyList<string> Words => List.Value.Words;

    /// <summary>
    /// The embedded wordlist with LF line endings: the file exactly as published. A Windows checkout
    /// with <c>core.autocrlf</c> may have turned its LFs into CRLFs before the build embedded it;
    /// that one conversion is undone here, and the result must still match <see cref="WordlistSha256"/>.
    /// </summary>
    public static byte[] WordlistBytes() => UndoCrlf(ReadResource());

    /// <summary>Replaces every CR LF pair with LF; other bytes are left alone.</summary>
    internal static byte[] UndoCrlf(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (Array.IndexOf(raw, (byte)'\r') < 0) return raw;
        var output = new List<byte>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n') continue;
            output.Add(raw[i]);
        }
        return output.ToArray();
    }

    /// <summary>The 24 words for a 32-byte key. Fails with <c>badKeyLength</c> for any other length.</summary>
    public static IReadOnlyList<string> Encode(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new SyncException(SyncErrorCodes.BadKeyLength);
        var bits = new byte[33];
        key.CopyTo(bits);
        bits[32] = SHA256.HashData(key)[0];
        var words = new string[WordCount];
        for (var i = 0; i < WordCount; i++)
        {
            words[i] = List.Value.Words[ReadBits(bits, i * 11, 11)];
        }
        return words;
    }

    /// <summary>The phrase as shown: the words joined by single spaces.</summary>
    public static string EncodeToString(ReadOnlySpan<byte> key) => string.Join(' ', Encode(key));

    /// <summary>
    /// Lowercases <c>A</c>–<c>Z</c> only and splits on runs of the five separators. Every other
    /// character, including other Unicode spaces, stays part of a word.
    /// </summary>
    public static IReadOnlyList<string> Normalize(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in phrase)
        {
            var c = raw is >= 'A' and <= 'Z' ? (char)(raw + 32) : raw;
            if (Separators.Contains(c, StringComparison.Ordinal))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// <summary>
    /// Decodes a typed phrase to the 32-byte key, failing with <c>wrongWordCount</c>,
    /// <c>unknownWord</c> or <c>badChecksum</c>. Confirm the key against vault.json's key check
    /// values afterwards (<see cref="Files.VaultFile.MatchGeneration"/>).
    /// </summary>
    public static byte[] Decode(string phrase)
    {
        var words = Normalize(phrase);
        if (words.Count != WordCount) throw new SyncException(SyncErrorCodes.WrongWordCount);
        var bits = new byte[33];
        for (var i = 0; i < WordCount; i++)
        {
            if (!List.Value.Index.TryGetValue(words[i], out var index)) throw new SyncException(SyncErrorCodes.UnknownWord);
            WriteBits(bits, i * 11, 11, index);
        }
        var key = bits.AsSpan(0, 32).ToArray();
        if (SHA256.HashData(key)[0] != bits[32]) throw new SyncException(SyncErrorCodes.BadChecksum);
        return key;
    }

    private static int ReadBits(byte[] data, int start, int count)
    {
        var v = 0;
        for (var i = 0; i < count; i++)
        {
            var bit = start + i;
            v = (v << 1) | ((data[bit >> 3] >> (7 - (bit & 7))) & 1);
        }
        return v;
    }

    private static void WriteBits(byte[] data, int start, int count, int value)
    {
        for (var i = 0; i < count; i++)
        {
            var bit = start + i;
            if (((value >> (count - 1 - i)) & 1) != 0)
            {
                data[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
            }
        }
    }

    private static byte[] ReadResource()
    {
        using var stream = typeof(RecoveryPhrase).Assembly.GetManifestResourceStream(WordlistResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {WordlistResourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Wordlist LoadWordlist()
    {
        var raw = WordlistBytes();
        var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        if (hash != WordlistSha256)
        {
            throw new InvalidOperationException("The embedded BIP39 wordlist does not match its pinned SHA-256.");
        }
        var words = Encoding.ASCII.GetString(raw).Split('\n');
        if (words.Length > 0 && words[^1].Length == 0) words = words[..^1];
        if (words.Length != 2048) throw new InvalidOperationException("The BIP39 wordlist must have 2048 words.");
        var index = new Dictionary<string, int>(2048, StringComparer.Ordinal);
        for (var i = 0; i < words.Length; i++) index.Add(words[i], i);
        return new Wordlist(words, index);
    }

    private sealed record Wordlist(string[] Words, Dictionary<string, int> Index);
}
