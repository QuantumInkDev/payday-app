using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using PayDay.Sync.Engine;
using PayDay.Sync.Storage;

namespace PayDay.Tests.Sync.Local;

/// <summary>
/// Temporary SQLite databases for tests: each is a new file in the temp folder (or a private
/// in-memory database), deleted when the owner is disposed. Connections are not pooled so the files
/// can be deleted on Windows too.
/// </summary>
internal sealed class SqliteTestFiles : IDisposable
{
    private readonly List<string> _files = new();
    private readonly List<IDisposable> _owned = new();

    /// <summary>A path for a database file that does not exist yet.</summary>
    public string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"payday-test-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    public static string ConnectionString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
        => new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

    /// <summary>Sync storage (tables created) on a new temporary file.</summary>
    public SqliteSyncStorage NewFileStorage() => Own(WithSchema(new SqliteSyncStorage(ConnectionString(NewPath()))));

    /// <summary>Sync storage (tables created) on a private in-memory database.</summary>
    public SqliteSyncStorage NewMemoryStorage() => Own(WithSchema(new SqliteSyncStorage("Data Source=:memory:")));

    public T Own<T>(T item) where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }

    private static SqliteSyncStorage WithSchema(SqliteSyncStorage storage)
    {
        storage.CreateSchema();
        return storage;
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var path in _files)
        {
            foreach (var file in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch (IOException)
                {
                    // Best effort: a temp file left behind is harmless.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

/// <summary><see cref="PayDay.Tests.Sync.SyncReplicaTests"/> on SQLite storage in a temporary file.</summary>
public sealed class SqliteFileSyncReplicaTests : PayDay.Tests.Sync.SyncReplicaTests, IDisposable
{
    private readonly SqliteTestFiles _files = new();

    protected override ISyncStorage NewStorage() => _files.NewFileStorage();

    public void Dispose() => _files.Dispose();
}

/// <summary><see cref="PayDay.Tests.Sync.SyncReplicaTests"/> on SQLite storage in memory.</summary>
public sealed class SqliteMemorySyncReplicaTests : PayDay.Tests.Sync.SyncReplicaTests, IDisposable
{
    private readonly SqliteTestFiles _files = new();

    protected override ISyncStorage NewStorage() => _files.NewMemoryStorage();

    public void Dispose() => _files.Dispose();
}

/// <summary><see cref="PayDay.Tests.Sync.SyncEngineTests"/> on SQLite storage in a temporary file.</summary>
public sealed class SqliteFileSyncEngineTests : PayDay.Tests.Sync.SyncEngineTests, IDisposable
{
    private readonly SqliteTestFiles _files = new();

    protected override ISyncStorage NewStorage() => _files.NewFileStorage();

    public void Dispose() => _files.Dispose();
}
