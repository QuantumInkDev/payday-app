using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PayDay.Sync.Json;

/// <summary>
/// The strict JSON reading and writing rules of <c>spec/sync-protocol.md</c> section 6.3, used for
/// every decrypted payload and for <c>vault.json</c>.
/// </summary>
public static class StrictJson
{
    /// <summary>The largest integer every platform can hold exactly in a binary64: 2^53 − 1.</summary>
    public const long MaxSafeInteger = (1L << 53) - 1;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 1000,
    };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    /// <summary>
    /// Parses exactly one JSON value from UTF-8 bytes, or throws <see cref="SyncException"/> with
    /// code <c>malformed</c> if the bytes start with a byte-order mark, are not valid UTF-8, hold
    /// anything but one JSON value (whitespace around it is fine), repeat a key in any object,
    /// contain a string escape that leaves an unpaired UTF-16 surrogate, or use a non-JSON token
    /// such as <c>NaN</c>.
    /// </summary>
    public static JsonData Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            throw new SyncException(SyncErrorCodes.Malformed, "byte-order mark");
        }
        try
        {
            StrictUtf8.GetCharCount(utf8);
        }
        catch (DecoderFallbackException ex)
        {
            throw new SyncException(SyncErrorCodes.Malformed, "invalid UTF-8", ex);
        }

        try
        {
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read())
            {
                throw new SyncException(SyncErrorCodes.Malformed, "no JSON value");
            }
            var value = ReadValue(ref reader);
            if (reader.Read())
            {
                throw new SyncException(SyncErrorCodes.Malformed, "text after the JSON value");
            }
            return value;
        }
        catch (JsonException ex)
        {
            throw new SyncException(SyncErrorCodes.Malformed, ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            // Utf8JsonReader.GetString throws this for escapes that leave invalid UTF-16.
            throw new SyncException(SyncErrorCodes.Malformed, ex.Message, ex);
        }
    }

    /// <summary>Parses JSON text: the string's UTF-8 encoding goes through <see cref="Parse(ReadOnlySpan{byte})"/>.</summary>
    public static JsonData Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Reads a protocol integer field (section 6.3): any JSON number that is an integer within
    /// ±(2^53 − 1) once parsed to binary64. Booleans, strings, null, fractions, infinities and a
    /// missing value are not integers.
    /// </summary>
    public static bool TryGetInteger(JsonData? value, out long result)
    {
        result = 0;
        if (value is null || value.Kind != JsonDataKind.Number) return false;
        var d = value.NumberValue;
        if (!double.IsFinite(d) || Math.Floor(d) != d || Math.Abs(d) > MaxSafeInteger) return false;
        result = (long)d;
        return true;
    }

    /// <summary>Writes a value as compact UTF-8 JSON. Non-finite numbers cannot be written.</summary>
    public static byte[] Serialize(JsonData value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(writer, value);
        }
        return buffer.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonData value)
    {
        switch (value.Kind)
        {
            case JsonDataKind.Null:
                writer.WriteNullValue();
                break;
            case JsonDataKind.Bool:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case JsonDataKind.Number:
                WriteNumber(writer, value.NumberValue);
                break;
            case JsonDataKind.String:
                writer.WriteStringValue(value.StringValue);
                break;
            case JsonDataKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.Items) Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStartObject();
                foreach (var (key, member) in value.Members)
                {
                    writer.WritePropertyName(key);
                    Write(writer, member);
                }
                writer.WriteEndObject();
                break;
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, double d)
    {
        if (!double.IsFinite(d))
        {
            throw new ArgumentOutOfRangeException(nameof(d), d, "JSON numbers must be finite.");
        }
        // Integers are written as plain digits (section 6.3: never 3.0 or 3e0).
        if (Math.Floor(d) == d && Math.Abs(d) <= MaxSafeInteger)
        {
            writer.WriteNumberValue((long)d);
        }
        else
        {
            writer.WriteNumberValue(d);
        }
    }

    private static JsonData ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var members = new List<KeyValuePair<string, JsonData>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    Next(ref reader);
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    var key = CheckedString(ref reader);
                    if (!seen.Add(key))
                    {
                        throw new SyncException(SyncErrorCodes.Malformed, $"duplicate key \"{key}\"");
                    }
                    Next(ref reader);
                    members.Add(new KeyValuePair<string, JsonData>(key, ReadValue(ref reader)));
                }
                return JsonData.Object(members);
            }
            case JsonTokenType.StartArray:
            {
                var items = new List<JsonData>();
                while (true)
                {
                    Next(ref reader);
                    if (reader.TokenType == JsonTokenType.EndArray) break;
                    items.Add(ReadValue(ref reader));
                }
                return JsonData.Array(items);
            }
            case JsonTokenType.String:
                return JsonData.String(CheckedString(ref reader));
            case JsonTokenType.Number:
            {
                // double.Parse rounds to nearest like JSON.parse and gives an infinity on overflow.
                var text = Encoding.ASCII.GetString(reader.ValueSpan);
                return JsonData.Number(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            case JsonTokenType.True:
                return JsonData.True;
            case JsonTokenType.False:
                return JsonData.False;
            case JsonTokenType.Null:
                return JsonData.Null;
            default:
                throw new SyncException(SyncErrorCodes.Malformed, $"unexpected token {reader.TokenType}");
        }
    }

    private static void Next(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new SyncException(SyncErrorCodes.Malformed, "unexpected end of JSON");
        }
    }

    private static string CheckedString(ref Utf8JsonReader reader)
    {
        var s = reader.GetString() ?? throw new SyncException(SyncErrorCodes.Malformed, "null string");
        if (HasLoneSurrogate(s))
        {
            throw new SyncException(SyncErrorCodes.Malformed, "unpaired surrogate");
        }
        return s;
    }

    /// <summary>True when the string holds a UTF-16 surrogate that is not part of a valid pair.</summary>
    public static bool HasLoneSurrogate(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                    continue;
                }
                return true;
            }
            if (char.IsLowSurrogate(c)) return true;
        }
        return false;
    }
}
