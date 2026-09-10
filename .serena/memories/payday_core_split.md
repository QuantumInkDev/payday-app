# Why PayDay is split into PayDay + PayDay.Core

**Rule:** `PayDay.Core` must NOT reference `Microsoft.WindowsAppSDK`, WinUI, or any package that pulls them in. Tests reference `PayDay.Core` only — never `PayDay.csproj`.

**Why this exists:**
- `Microsoft.WindowsAppSDK` injects auto-initializer module constructors (via `Microsoft.WindowsAppRuntime.Bootstrap`).
- Those auto-initializers demand **package identity** at process start.
- Any non-packaged host process (xunit test runner, `dotnet run` on a plain library, a console probe) crashes at module init with:
  ```
  System.Runtime.InteropServices.COMException (0x80040154)
  REGDB_E_CLASSNOTREG
  ```
- There is no flag to disable the bootstrapper. The only fix is to keep WinAppSDK-tainted assemblies out of the test process entirely.

**Therefore:**
- All models (`Bill`, `Payment`, `PayPeriod`, `PeriodBill`, `Snapshot`) live in `PayDay.Core/Models/`.
- All pure / DB-abstracted business logic (`PayPeriodService`, `PaymentService`, `PayoffCalculator`) lives in `PayDay.Core/Services/`.
- The DB seam is the `IDatabaseService` interface (also in `PayDay.Core/Services/`).
- The concrete `DatabaseService` (which talks to SQLite + sits in a process with WinAppSDK) stays in `PayDay/Services/` and implements `IDatabaseService`.
- `PayDay.Tests` references `PayDay.Core` only and uses fake `IDatabaseService` implementations where needed.

**How to apply:** Before adding new code, ask "does this need WinUI types or DispatcherQueue?" If no → put it in `PayDay.Core`. If yes → put it in `PayDay/`, but extract any pure-data helpers back to `PayDay.Core` so they can be unit-tested.

See also [[suggested_commands]] for the build/test commands, and [[code_style]] for the DI-seam pattern.
