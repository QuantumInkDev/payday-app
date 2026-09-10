# Code Style & Conventions

## File / type layout

- **File-scoped namespaces** everywhere: `namespace PayDay.Models;` on line 1.
- One public type per file, file name matches type name.
- Classes default to `sealed` unless inheritance is intended.
- Models prefer plain mutable `class` for entities persisted by ADO.NET (e.g. `Bill`, `Payment`) and `record` for pure value types (e.g. `PayPeriod`, `PeriodBill`, `AssignedPayPeriod`).

## C# language features

- `<Nullable>enable</Nullable>` — nullable reference types are on. Use `string?` for genuinely optional, `string` with default initializer (`= string.Empty`) for required-but-unset.
- `<ImplicitUsings>enable</ImplicitUsings>` — `System`, `System.Linq`, etc. are auto-imported, but services still explicitly `using System;` etc. when the file pulls in non-implicit namespaces (see `PayPeriodService.cs`).
- Expression-bodied members for one-liner constructors: `public PayPeriodService(IDatabaseService db) => _db = db;`.
- `_camelCase` for private fields.
- `PascalCase` for public properties, methods, constants.
- `const` for compile-time constants (e.g. `PeriodLengthDays = 14`), `static readonly` for runtime-initialized (e.g. `DefaultAnchor = new DateTime(2025, 3, 20)`).

## Documentation

- `///` XML doc comments on public methods in `PayDay.Core/Services/*` — especially for ports of the JS prototype, where the doc cites the JS function it mirrors. Example: `// Mirrors the JS getPayPeriods() in payday.html.`
- Section dividers in long services use `// ----` comment bars (see `PayPeriodService.cs`).
- Don't comment WHAT the code does; do comment WHY a non-obvious port detail exists. Example worth keeping: `// JS: new Date(year, month, day) overflows; mirror with .AddDays(day - 1).`

## Architecture patterns

- **DI seam via interface**: `IDatabaseService` lives in `PayDay.Core`. The WinUI side's `DatabaseService` implements it. Tests inject fakes.
- **Pure-function-first**: business services expose `static` pure methods (e.g. `PayPeriodService.GetPayPeriods`, `GetBillDueDate`, `AssignBillsToPeriods`) so they're testable without DB. Stateful methods (`GetCurrentPeriodsAsync` etc.) take the `IDatabaseService` in the constructor.
- **MVVM (Phase 4 onward)**: pages use `CommunityToolkit.Mvvm` — `ObservableObject` base, `[ObservableProperty]` + `[RelayCommand]` source generators, XAML `x:Bind` (not `Binding`).

## Test conventions

- xUnit `[Fact]` / `[Theory]` + `[InlineData]`. Test names: `MethodUnderTest_Scenario_ExpectedBehavior` (e.g. `GetBillDueDate_Day31InFebWindow_OverflowsToMarch3`).
- One test class per service: `PayPeriodServiceTests.cs`, `PayoffCalculatorTests.cs`.
- Tests target `PayDay.Core` only. Never reference the WinUI csproj — see [[payday_core_split]].

## Things to avoid

- Don't reference WinUI / WindowsAppSDK types from `PayDay.Core`. The whole point of the split is that Core stays runnable in plain .NET.
- Don't `using static` or wildcards in pure-logic files — they make ports from the JS prototype harder to audit line-by-line.
- Don't add code comments that describe WHAT (the well-named identifiers already do that) or that reference the current task/PR (rot fast).
