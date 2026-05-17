using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PayDay.Models;

namespace PayDay.Services;

/// <summary>
/// Round-trippable JSON serializer for the full PayDay dataset. Used by the
/// Settings page (plan §4.7 / §6.1) for manual JSON export + import.
///
/// Versioned so future schema bumps can branch on the file's <c>FormatVersion</c>.
/// </summary>
public static class BackupSerializer
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ToJson(
        IReadOnlyList<Bill> bills,
        IReadOnlyList<Payment> payments,
        IReadOnlyList<Snapshot> snapshots,
        IReadOnlyDictionary<string, string?> settings,
        DateTime? exportedAt = null)
    {
        var file = new BackupFile
        {
            FormatVersion = CurrentFormatVersion,
            ExportedAt = (exportedAt ?? DateTime.UtcNow).ToString("o"),
            Bills = bills.ToList(),
            Payments = payments.ToList(),
            Snapshots = snapshots.ToList(),
            Settings = settings.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        return JsonSerializer.Serialize(file, Options);
    }

    public static BackupFile FromJson(string json)
    {
        var migrated = MigrateLegacyBillKeys(json);
        var parsed = JsonSerializer.Deserialize<BackupFile>(migrated, Options)
            ?? throw new InvalidOperationException("Backup file is empty.");
        if (parsed.FormatVersion <= 0)
        {
            throw new InvalidOperationException("Backup file is missing a formatVersion.");
        }
        if (parsed.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Backup file uses format version {parsed.FormatVersion}, " +
                $"but this build only understands up to {CurrentFormatVersion}.");
        }
        parsed.Bills ??= new List<Bill>();
        parsed.Payments ??= new List<Payment>();
        parsed.Snapshots ??= new List<Snapshot>();
        parsed.Settings ??= new Dictionary<string, string?>();
        return parsed;
    }

    /// <summary>
    /// Older v1 backups (pre-Phase-7c) stored <c>cost</c> and <c>owed</c> keys
    /// inside each bill. After the Cost→Payment / Owed→Remaining rename, those
    /// keys no longer map to anything. Rewrite legacy keys to the new names so
    /// existing backup files keep restoring correctly.
    /// </summary>
    private static string MigrateLegacyBillKeys(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("bills", out var billsEl) || billsEl.ValueKind != JsonValueKind.Array)
        {
            return json;
        }

        var needsRewrite = false;
        foreach (var bill in billsEl.EnumerateArray())
        {
            if (bill.ValueKind != JsonValueKind.Object) continue;
            if (bill.TryGetProperty("cost", out _) || bill.TryGetProperty("owed", out _))
            {
                needsRewrite = true;
                break;
            }
        }
        if (!needsRewrite) return json;

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteRewrittenRoot(doc.RootElement, writer);
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRewrittenRoot(JsonElement root, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("bills") && prop.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("bills");
                writer.WriteStartArray();
                foreach (var bill in prop.Value.EnumerateArray())
                {
                    WriteRewrittenBill(bill, writer);
                }
                writer.WriteEndArray();
            }
            else
            {
                prop.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteRewrittenBill(JsonElement bill, Utf8JsonWriter writer)
    {
        if (bill.ValueKind != JsonValueKind.Object)
        {
            bill.WriteTo(writer);
            return;
        }
        writer.WriteStartObject();
        foreach (var prop in bill.EnumerateObject())
        {
            var name = prop.NameEquals("cost") ? "payment"
                     : prop.NameEquals("owed") ? "remaining"
                     : prop.Name;
            writer.WritePropertyName(name);
            prop.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}

public sealed class BackupFile
{
    public int FormatVersion { get; set; }
    public string ExportedAt { get; set; } = string.Empty;
    public List<Bill> Bills { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();
    public List<Snapshot> Snapshots { get; set; } = new();
    public Dictionary<string, string?> Settings { get; set; } = new();
}
