using System;
using System.IO;
using Windows.Storage;

namespace PayDay.Services;

/// <summary>
/// The app's <c>payday.db</c> in the package's LocalFolder, seeded on first launch. All SQL lives in
/// <see cref="SqliteDatabaseService"/> (PayDay.Core) so it can be tested without WinUI.
/// </summary>
public sealed class DatabaseService : SqliteDatabaseService
{
    private static readonly Lazy<DatabaseService> _instance = new(() => new DatabaseService());
    public static DatabaseService Instance => _instance.Value;

    private DatabaseService()
        : base(Path.Combine(ApplicationData.Current.LocalFolder.Path, "payday.db"), SeedData.Bills, SeedData.DefaultSettings)
    {
    }
}
