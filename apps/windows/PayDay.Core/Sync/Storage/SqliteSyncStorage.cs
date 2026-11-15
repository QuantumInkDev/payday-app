using Microsoft.Data.Sqlite;
using PayDay.Sync.Engine;
using PayDay.Sync.Merge;

namespace PayDay.Sync.Storage;

/// <summary>
/// <see cref="ISyncStorage"/> on SQLite: the sync tables of <see cref="SqliteSyncSchema"/> in the same
/// database file as the app's tables, through <b>one dedicated connection</b> that only the sync layer
/// uses. <see cref="RunInTransaction"/> wraps the action in one <c>BEGIN IMMEDIATE</c> … <c>COMMIT</c>
/// (rolled back when the action throws) and is not reentrant, as <see cref="ISyncStorage"/> requires.
/// Code that must commit with the merge (the table materializer, the migration) runs its SQL through
/// <see cref="CreateCommand"/>, which enlists in the running transaction.
/// <para>
/// Not thread-safe, like every <see cref="ISyncStorage"/>: <see cref="SyncReplica"/> serializes access.
/// Opening the storage creates no table; call <see cref="CreateSchema"/> (normally inside the
/// transaction that turns sync on).
/// </para>
/// </summary>
public sealed class SqliteSyncStorage : ISyncStorage, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);
    private readonly SqliteRecordStore _records;
    private readonly SqliteSyncMetadata _metadata;
    private SqliteTransaction? _transaction;
    private bool _disposed;

    /// <summary>Opens the dedicated sync connection (foreign keys on, like the app's connections). Creates no table.</summary>
    public SqliteSyncStorage(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }
        _records = new SqliteRecordStore(this);
        _metadata = new SqliteSyncMetadata(this);
    }

    public IRecordStore Records => _records;

    public ISyncMetadata Metadata => _metadata;

    /// <summary>True while <see cref="RunInTransaction"/> is running.</summary>
    public bool InTransaction => _transaction is not null;

    /// <summary>True when the sync tables exist. Read-only; call it outside a transaction.</summary>
    public bool SchemaExists() => SqliteSyncSchema.SchemaExists(OutsideTransaction());

    /// <summary>True when local sync is turned on in this database. Read-only; call it outside a transaction.</summary>
    public bool IsSyncEnabled() => SqliteSyncSchema.IsSyncEnabled(OutsideTransaction());

    private SqliteConnection OutsideTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transaction is not null) throw new InvalidOperationException("Call this outside a sync transaction.");
        return _connection;
    }

    /// <summary>Creates the sync tables if they do not exist (inside the running transaction, if any).</summary>
    public void CreateSchema()
    {
        using var cmd = CreateCommand(SqliteSyncSchema.Ddl);
        cmd.ExecuteNonQuery();
    }

    public void RunInTransaction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transaction is not null)
        {
            throw new InvalidOperationException("RunInTransaction is not reentrant: a sync transaction is already running on this storage.");
        }
        _transaction = _connection.BeginTransaction(deferred: false);
        try
        {
            action();
            _transaction.Commit();
        }
        catch
        {
            _transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    /// <summary>
    /// A new command on the sync connection, enlisted in the running transaction (if any). The caller
    /// disposes it. Use this for SQL that must commit or roll back with the sync state.
    /// </summary>
    public SqliteCommand CreateCommand(string sql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _transaction;
        return cmd;
    }

    /// <summary>
    /// A cached, reusable command for <paramref name="sql"/> with the named parameters, enlisted in the
    /// running transaction. Not disposed by the caller; parameters are set by name before each use.
    /// </summary>
    internal SqliteCommand Cached(string sql, params string[] parameterNames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_commands.TryGetValue(sql, out var cmd))
        {
            cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var name in parameterNames) cmd.Parameters.Add(new SqliteParameter(name, null));
            _commands[sql] = cmd;
        }
        cmd.Transaction = _transaction;
        return cmd;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transaction?.Dispose();
        _transaction = null;
        foreach (var cmd in _commands.Values) cmd.Dispose();
        _commands.Clear();
        _connection.Dispose();
    }
}
