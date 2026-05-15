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
        var parsed = JsonSerializer.Deserialize<BackupFile>(json, Options)
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
