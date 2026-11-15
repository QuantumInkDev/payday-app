# Tech Stack

**Language / runtime:** C# 12, .NET 9.0. WinUI app targets `net9.0-windows10.0.26100.0`; Core + Tests target plain `net9.0`.

**Project-wide MSBuild settings:** `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`.

**Key NuGet packages (PayDay app):**
- `Microsoft.WindowsAppSDK` 2.0.1 — WinUI 3 framework (requires matching runtime package installed system-wide).
- `Microsoft.Windows.SDK.BuildTools` 10.0.28000.1839
- `Microsoft.Windows.SDK.BuildTools.WinApp` 0.3.1 — adds `dotnet run` support + `winapp` CLI for packaged apps.
- `CommunityToolkit.Mvvm` 8.4.2 — `ObservableObject`, `RelayCommand`, source generators for `[ObservableProperty]`.
- `LiveChartsCore.SkiaSharpView.WinUI` 2.0.2 — charts (Phase 5+).
- `Microsoft.Data.Sqlite` 10.0.8 — local DB.
- `System.Net.Http.Json` 10.0.8 — for Notion sync (future phase).

**Test stack (PayDay.Tests):**
- `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2, `Microsoft.NET.Test.Sdk` 17.12.0, `coverlet.collector` 6.0.2.

**Data layer:** SQLite via `Microsoft.Data.Sqlite`. DB lives in app LocalState — see [[suggested_commands]] for the path resolver. Schema seeded with 27 bills.

**Platforms:** `x86;x64;ARM64`. Output type `WinExe`. Packaged via single-project MSIX (`Package.appxmanifest`).

**Architectural seam:** `apps/windows/PayDay.Core/Services/IDatabaseService.cs` is the interface. `apps/windows/PayDay/Services/DatabaseService.cs` (the only WinUI-side service) implements it so Core services can be unit-tested without the WinAppSDK runtime.
