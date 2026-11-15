using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Services;

namespace PayDay.Tests;

/// <summary>
/// In-memory <see cref="IBackupStore"/> stand-in for <see cref="BackupRotationService"/>
/// tests. Files keep their full write history in <see cref="WriteHistory"/> for
/// assertion ergonomics.
/// </summary>
internal sealed class InMemoryBackupStore : IBackupStore
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    public List<string> WriteHistory { get; } = new();
    public List<string> DeleteHistory { get; } = new();

    public IReadOnlyDictionary<string, string> Snapshot()
        => _entries.ToDictionary(kv => kv.Key, kv => kv.Value.Content, StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam — pin a fixed <c>LastWriteUtc</c> on the next write.</summary>
    public DateTime? NextWriteTimestamp { get; set; }

    public Task WriteAsync(string fileName, string content, CancellationToken ct = default)
    {
        var stamp = NextWriteTimestamp ?? DateTime.UtcNow;
        NextWriteTimestamp = null;
        _entries[fileName] = new Entry(content, stamp);
        WriteHistory.Add(fileName);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BackupEntry>>(
            _entries.Select(kv => new BackupEntry(kv.Key, kv.Value.LastWriteUtc)).ToList());

    public Task<string> ReadAsync(string fileName, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(fileName, out var entry))
            throw new KeyNotFoundException($"No backup named '{fileName}'.");
        return Task.FromResult(entry.Content);
    }

    public Task DeleteAsync(string fileName, CancellationToken ct = default)
    {
        if (_entries.Remove(fileName))
            DeleteHistory.Add(fileName);
        return Task.CompletedTask;
    }

    /// <summary>Test helper — seed an existing backup without using <see cref="WriteAsync"/>.</summary>
    public void Seed(string fileName, string content, DateTime lastWriteUtc)
    {
        _entries[fileName] = new Entry(content, lastWriteUtc);
    }

    private sealed record Entry(string Content, DateTime LastWriteUtc);
}
