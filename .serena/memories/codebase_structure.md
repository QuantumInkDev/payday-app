# Codebase Structure

Repo root: `P:\Projects-Not-On-Cloud\PayDayApp`. No `.sln` file — build each `.csproj` directly with `dotnet`.

```
PayDayApp/
├─ CLAUDE.md                 # Project-level instructions (session protocol, git workflow)
├─ SESSION_STATUS.md         # Live status; always read first. Has 🔴 CONTINUE HERE marker.
├─ planning/
│  ├─ PAYDAY_WINUI3_PLAN.md  # Plan of record (phases 1–N)
│  ├─ current-sprint.md      # Active sprint; archive to sprint-NN-*.md when closing
│  ├─ sprint-01-bootstrap.md
│  ├─ sprint-02-data-layer.md
│  └─ payday.html            # Original JS prototype — port reference
├─ apps/windows/PayDay/                   # WinUI 3 app (net9.0-windows10.0.26100.0)
│  ├─ App.xaml(.cs)
│  ├─ MainWindow.xaml(.cs)
│  ├─ Package.appxmanifest
│  ├─ app.manifest
│  ├─ PayDay.csproj
│  ├─ Assets/                # Logos, splash, AppIcon.ico
│  ├─ Pages/                 # XAML pages (Phase 4: PayDayPage, AllBillsPage, etc.)
│  ├─ Models/                # WinUI-only models (most live in apps/windows/PayDay.Core/Models)
│  ├─ Properties/
│  └─ Services/              # DatabaseService.cs (implements PayDay.Core IDatabaseService)
├─ apps/windows/PayDay.Core/              # net9.0 class library — NO WinUI references allowed
│  ├─ PayDay.Core.csproj
│  ├─ Models/                # Bill, Payment, PayPeriod, PeriodBill, Snapshot
│  └─ Services/              # IDatabaseService, PayPeriodService, PaymentService, PayoffCalculator
└─ apps/windows/PayDay.Tests/             # xunit on net9.0; references PayDay.Core ONLY
   ├─ PayDay.Tests.csproj
   ├─ PayPeriodServiceTests.cs
   └─ PayoffCalculatorTests.cs
```

**Namespace convention:** `PayDay.Models`, `PayDay.Services` (used by both PayDay and PayDay.Core — same namespace, different assemblies). Root namespace in csproj is `PayDay`.

**Where to add code (Phase 4):**
- New page → `apps/windows/PayDay/Pages/{Name}Page.xaml` + `.xaml.cs`.
- ViewModel → `apps/windows/PayDay/ViewModels/{Name}PageViewModel.cs` (folder may need creating). Use `CommunityToolkit.Mvvm` `ObservableObject` + `[ObservableProperty]`/`[RelayCommand]` source generators.
- New model or pure logic → put it in `PayDay.Core` so it stays testable.

See [[payday_core_split]] for *why* Core is split from the app, and [[task_completion_checklist]] for the session-close protocol that's mandatory after any change.
