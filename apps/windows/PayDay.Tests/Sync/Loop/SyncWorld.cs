using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PayDay.Services;
using PayDay.Sync.Coordination;
using PayDay.Sync.Drive;
using PayDay.Sync.Keys;
using PayDay.Sync.Local;
using PayDay.Tests.Sync.Local;

namespace PayDay.Tests.Sync.Loop;

/// <summary>
/// Several simulated devices, each with its own <c>payday.db</c> (a separate SQLite file), local sync,
/// routing write path, key store and coordinator, sharing one <see cref="InMemoryDriveProvider"/>. One
/// world clock moves every device's clock and the drive's together; a device may start with a skewed
/// clock. All data is fictional.
/// </summary>
internal sealed class SyncWorld : IDisposable
{
    private readonly List<SyncDevice> _devices = new();
    private readonly List<string> _extraFiles = new();

    public SyncWorld(bool enforcesUniqueNames = true, bool deletesPermanently = true)
    {
        ServerClock = new ManualTimeProvider(SyncTestEnvironment.T0);
        Drive = new InMemoryDriveProvider(ServerClock, enforcesUniqueNames, deletesPermanently);
    }

    public ManualTimeProvider ServerClock { get; }

    public InMemoryDriveProvider Drive { get; }

    public IReadOnlyList<SyncDevice> Devices => _devices;

    /// <summary>A new device with today's tables (no seed data); <paramref name="clockSkewMs"/> sets its clock ahead (or behind).</summary>
    public async Task<SyncDevice> AddDeviceAsync(string name, long clockSkewMs = 0, bool notionToken = false, string? clonePath = null)
    {
        var device = new SyncDevice(this, name, clockSkewMs, notionToken, clonePath);
        _devices.Add(device);
        await device.Env.InitializeAsync();
        return device;
    }

    /// <summary>Copies a device's database file, as a cloned disk or restored app data would.</summary>
    public string CloneDatabase(SyncDevice device)
    {
        SqliteTestFiles.ClearPoolsFor(device.Env.Path);
        var copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"payday-test-clone-{Guid.NewGuid():N}.db");
        File.Copy(device.Env.Path, copy);
        _extraFiles.Add(copy);
        return copy;
    }

    /// <summary>Moves every clock (devices and drive) forward.</summary>
    public void Advance(long ms)
    {
        ServerClock.Advance(ms);
        foreach (var d in _devices) d.Env.Time.Advance(ms);
    }

    public void Dispose()
    {
        foreach (var d in _devices) d.Dispose();
        foreach (var f in _extraFiles)
        {
            SqliteTestFiles.ClearPoolsFor(f);
            try
            {
                if (File.Exists(f)) File.Delete(f);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>One simulated device of a <see cref="SyncWorld"/>.</summary>
internal sealed class SyncDevice : IDisposable
{
    public SyncDevice(SyncWorld world, string name, long clockSkewMs, bool notionToken, string? clonePath)
    {
        Name = name;
        Env = new SyncTestEnvironment(world.ServerClock.UnixMs + clockSkewMs, clonePath);
        if (notionToken) Credentials.Set(NotionSyncService.TokenKey, "secret_fictional_token");
        Coordinator = new SyncCoordinator(
            Env.Sync,
            world.Drive,
            Keys,
            Env.Backups,
            Env.Db,
            new DeviceInfo(name, "windows", "0.0.0-test"),
            () => Credentials.Exists(NotionSyncService.TokenKey),
            Env.Time);
    }

    public string Name { get; }

    public SyncTestEnvironment Env { get; }

    public InMemoryVaultKeyStore Keys { get; } = new();

    public InMemoryCredentialStore Credentials { get; } = new();

    public SyncCoordinator Coordinator { get; }

    public string DeviceId => Env.Sync.Replica!.DeviceId;

    public Task<SyncStatus> SyncAsync() => Coordinator.SyncAsync();

    /// <summary>Every merged record with its HLC: two devices that converged hold exactly the same list.</summary>
    public IReadOnlyList<string> Records()
        => Env.Sync.Replica!.Read((s, _) => s.AllRecords().Select(r => $"{r.Entity}/{r.Id}/{r.Field}={r.Value}@{r.Hlc}").ToList());

    /// <summary>
    /// What the pages see: every bill (without the local <c>UpdatedAt</c>), payments and snapshots
    /// without their local integer IDs, and the synced settings.
    /// </summary>
    public async Task<string> TablesAsync()
    {
        var lines = new List<string>();
        foreach (var b in (await Env.Db.GetAllBillsAsync()).OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            lines.Add(string.Join("|", "bill", b.Id, b.Name, b.Type, F(b.Payment), F(b.Remaining), F(b.Available), F(b.CreditLimit),
                b.DueDay.ToString(CultureInfo.InvariantCulture), b.Rate, F(b.APR), b.AutoPay, b.Active, b.YearlyDate, b.Notes, b.NotionPageId, b.CreatedAt));
        }
        foreach (var p in (await Env.Db.GetAllPaymentsAsync()).Select(p => string.Join("|", "payment", p.BillId, p.PeriodKey, F(p.AmountPaid), p.PaidAt, p.NotionPageId)).Order(StringComparer.Ordinal))
        {
            lines.Add(p);
        }
        foreach (var s in (await Env.Db.GetAllSnapshotsAsync()).Select(s => string.Join("|", "snapshot", s.SnapshotDate, F(s.TotalRemaining), s.Details, s.NotionPageId)).Order(StringComparer.Ordinal))
        {
            lines.Add(s);
        }
        var settings = await Env.Db.GetAllSettingsAsync();
        foreach (var key in TableMaterializer.SyncedSettingKeys.Order(StringComparer.Ordinal))
        {
            lines.Add($"setting|{key}|{(settings.TryGetValue(key, out var v) ? v : "<none>")}");
        }
        return string.Join("\n", lines);
    }

    private static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        Coordinator.Dispose();
        Env.Dispose();
    }
}
