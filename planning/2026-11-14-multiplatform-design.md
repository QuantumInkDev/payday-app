# PayDay Multi-Platform — Design

**Date:** 2026-11-14
**Status:** Approved 2026-11-14
**Scope:** Repository restructure, a cross-device sync layer, a simpler Notion connection, a
local-first web app, and the order in which the macOS, iOS, web and Android apps are built. The
new-feature roadmap and the product name are listed here but settled separately (see §9).

Choices the user made in conversation are marked **(decided)**. Choices Claude made are marked
**(Claude's call)** and can be overruled.

---

## 1. Intent

PayDay works well as a Windows app. The goal is to make it a product that follows a person across
every device: Windows (done), macOS, iOS and Android, all showing the same data, with no visible
"sync step". The end game is the public app stores **(decided)**, so setup has to be short enough
that a stranger gets through it, and security has to be good enough to hold their financial data.

Notion sync works but takes about ten manual steps (create an integration, copy a token, build
three databases, share each one). That is acceptable for the developer and a dealbreaker for
everyone else.

## 2. Goals, non-goals, anti-goals

**Goals**

- Data entered on any device shows up on the others without the user doing anything.
- Works fully offline; syncs when the network returns.
- Costs nothing to run at any number of users, for the user and the developer.
- The cloud provider never sees readable financial data (end-to-end encryption).
- A new user is set up in a few taps. Adding a second device takes about a minute.
- Data stays portable: JSON export/import keeps working on every platform.
- Notion stays available as an optional add-on, with a much simpler connection.

**Non-goals (this design)**

- Bank linking or transaction import.
- A web app that keeps data on a server. (The web app in §6.7 keeps data in the browser and the
  user's drive, like every other device.)
- Accounts, sign-up or a server holding user data.
- Sharing a plan with a partner (it's a candidate feature, §9; it is not built here). The sync
  format is location-agnostic (§6.3): v1 stores the vault in the drive's private app folder, and a
  later shared vault can live in an ordinary shared folder using the same format, so partners on
  different cloud accounts aren't ruled out.

**Anti-goals** — a result that works technically but fails:

- Any path where the developer or the cloud provider can read a user's data.
- A sync that silently drops or reverts an edit.
- Setup longer than the current Notion flow.
- The four apps disagreeing about the numbers (which bills fall in which pay period, totals, payoff
  dates).

## 3. Constraints

- The Windows app is WinUI 3 / .NET 9 (`net9.0-windows10.0.26100.0`), used daily on an arm64 VM.
  It can only be built on Windows. This Mac has no `dotnet`; it has Xcode and Swift.
- **Windows builds run on the VM from the Mac.** The arm64 VM is the Parallels guest
  "Win 11 Arm Dev" on this Mac. `prlctl exec "Win 11 Arm Dev" …` runs commands in it without SSH
  (verified 2026-11-14: .NET SDK 9.0.318, clone at `C:\Projects\PayDayApp`). Those commands run as
  `NT AUTHORITY\SYSTEM`, so build and test work but launching the packaged app does not; that
  still needs the interactive session (Parallels' `--current-user` option is worth trying). Remote
  builds use `tools/vm-build.sh`, which keeps its own checkout at `C:\Builds\payday-app` and never touches
  the daily clone `C:\Projects\PayDayApp`; push before running it.
- Hosting is on the paid Vercel plan of the Quantum Ink team, so commercial use is covered.
- Browsers can erase a site's stored data (Safari after 7 days without a visit unless the site is
  installed). That is how the original `payday.html` lost data, and it shapes the web app (§6.7).
- `PayDay.Core` and `PayDay.Tests` are plain `net9.0`, with 194 passing tests that encode current
  behaviour (pay periods incl. the 3 PM early start, payoff math, backup format, Notion mapping).
- Free hosted databases/sync services all pause or delete idle free projects (Supabase, PowerSync,
  Appwrite, Couchbase); ElectricSQL is winding down, Realm sync is shut down. Research,
  2026-11-14, is summarised in the conversation; this is why the design uses the user's own cloud
  storage.
- iCloud Drive has no supported API on Windows or Android.
- The Notion OAuth token exchange needs a client secret that cannot ship inside an app.
- App-store distribution needs a privacy policy URL, and Notion's public-integration form needs a
  privacy policy and terms URL.
- Existing data must survive: the developer's live Windows database and Notion workspace are the
  starting point.

## 4. Approaches considered

### 4.1 Where synced data lives

| Approach | Trade-off |
|---|---|
| **User's own cloud drive, encrypted change files (picked, decided)** | $0 at any scale, provider sees only ciphertext, no accounts. We write and test the merge logic ourselves; we maintain 2–3 drive integrations. |
| Small relay service we run, stores only ciphertext | Simplest onboarding (no drive sign-in), but a service to run and pay for, and the same merge logic to write. |
| PowerSync + Supabase | Most finished engine, but ~$75/month or self-hosting, accounts required, and E2E encryption defeats most of what it offers. |

Picked because the user is fine with personal cloud sync, it keeps the "free and nobody can read
your data" promise, and the data is small (tens of bills, hundreds of payments a year).

### 4.2 Sharing the core logic across four apps

| Approach | Trade-off |
|---|---|
| **Native code per platform + a shared conformance suite (picked, decided)** | Each app idiomatic in its own language; existing C# core and tests carry over. Logic is written three times (C#, Swift, Kotlin), so drift is possible — caught by shared test fixtures every app must pass. |
| One Rust core with bindings to all four apps | Logic written once, can't drift. New language, harder cross-platform builds and debugging, and the C# core gets rewritten. |
| Kotlin Multiplatform core | Covers Android/iOS/macOS but not Windows on arm64 — rejected. |

### 4.3 Simplifying Notion

| Approach | Trade-off |
|---|---|
| **Notion public integration with OAuth + app creates its own databases (picked, Claude's call)** | Three clicks for the user. Needs a tiny stateless function to hold the client secret. |
| Keep the token-paste flow, add a guided wizard | No function to host, but still ~6 steps. |
| OAuth with template duplication | Works, but Notion now recommends creating content through the API; templates drift from code. |

### 4.4 The web app

| Approach | Trade-off |
|---|---|
| **TypeScript local-first web app, static on Vercel (picked; web app decided, TypeScript is Claude's call)** | Matches the Next.js-on-Vercel stack already used for SpellThat, and has the most mature browser tooling (SQLite in WebAssembly, WebCrypto, drive sign-in without a secret). A fourth language must pass the conformance suite. |
| Blazor WebAssembly reusing `PayDay.Core` | The C# rules aren't rewritten, but the screens are rebuilt anyway, the first download is several MB, and browser SQLite persistence is fiddlier. |
| Server-rendered app with a hosted database | Breaks "data local to the user" and "nobody else can read it". Rejected. |

## 5. Repository layout

The repository moved from `~/Projects/Windows/PayDayApp` to `~/Projects/PayDayApp`
**(decided, done 2026-11-14)**; a symlink remains at the old path so open sessions keep working.
The GitHub repo (`QuantumInkDev/payday-app`) stays the same.

Target layout **(Claude's call)**:

```
PayDayApp/
├── apps/
│   ├── windows/          # PayDay/, PayDay.Core/, PayDay.Tests/ — moved as-is with git mv
│   ├── apple/            # one Xcode project: macOS + iOS app targets
│   │   └── PayDayKit/    # Swift package: models, rules, sync engine, view-models
│   ├── android/          # Gradle project (later)
│   └── web/              # TypeScript web app + its Vercel functions (Notion OAuth exchange)
├── spec/
│   ├── data-model.md     # canonical entities, fields, units, ID rules
│   ├── sync-protocol.md  # file layout, encryption, merge rules
│   └── conformance/      # JSON fixtures every app's tests must pass
├── design/               # icon sources, palette, type colours, store screenshots
├── tools/                # existing PowerShell helpers
├── planning/
└── CLAUDE.md, SESSION_STATUS.md, README.md
```

- ~~No Turborepo/Nx~~ **Changed 2026-11-27:** Turborepo + pnpm now run the JavaScript workspaces
  (`apps/site`, later `apps/web`, `packages/*`) in this one repo; the native apps keep their own
  builds. See `planning/2026-11-27-site-and-monorepo.md`.
- The move must keep `git log --follow` history and leave the Windows build at 0 warnings / 0
  errors and 194/194 tests (verified on the arm64 VM, since this Mac can't build WinUI).
- The VM daily clone and the rig follow the ordered update steps in `SESSION_STATUS.md` (back up data first; don't just pull and delete).

## 6. Sync design

### 6.1 Data model changes (all platforms)

- Every synced row gets a globally unique ID. Bills added in the app use GUID strings, but the
  seeded bills use `"1"`–`"27"` (`apps/windows/PayDay/Services/SeedData.cs`). **Seed IDs are rewritten to GUIDs,
  and Payments and Snapshots move from `INTEGER AUTOINCREMENT` to string IDs (UUIDv7), Claude's
  call**, so rows created on two devices never collide. The migration rewrites references
  (`Payments.BillId`) to match.
- **Fresh installs start empty (Claude's call).** `SeedData.cs` now holds fictional sample data. Real
  personal data must never ship in a store build or be committed to this public repo. Seed data moves to a dev-only import
  or demo mode.
- Every synced row carries, per field, the time it was last changed and by which device, using a
  hybrid logical clock (HLC: wall-clock time plus a counter, so ordering survives clock skew).
- Deletes become tombstones (a "deleted at" marker) instead of row removal. This also solves the
  existing "Notion archive-on-delete needs a tombstone table" carryover.
- Settings split into **synced** (pay anchor date, early-start, type colours, custom types) and
  **per-device** (theme, window size, Notion token, drive sign-in). The canonical list lives in
  `spec/data-model.md`.
- Money stays as it is today (`double`) unless the conformance suite shows rounding differences
  between platforms; if it does, switch to integer cents everywhere (left to the builder, with
  that test as the trigger).

### 6.2 Merge rule

- **Last write wins per field**, ordered by HLC, ties broken by device ID. Two devices editing
  different fields of the same bill both keep their edits; the same field resolves to the later
  edit.
- **Remaining becomes a balance checkpoint plus payments (Claude's call).** Today "mark paid"
  lowers Remaining in place and unmark adds it back (`PayDayPageViewModel.cs`). Under
  last-write-wins that loses decrements: two offline devices paying the same card for different
  periods would each write "start minus one payment", and a statement figure typed in Notion would
  wipe out app payments. Instead each bill stores a **balance checkpoint** (amount + the time it
  was set) and
  `Remaining = checkpoint − payments recorded after the checkpoint`, clamped at zero.
  Editing Remaining (in the app or in Notion) sets a new checkpoint. What the user sees doesn't
  change; Notion's Remaining column is written as the computed value. Only a real edit sets a
  checkpoint: the connector's own write-back to Notion (an echo) and any edit that leaves the value
  unchanged never do, or an offline payment from another device could silently drop out of
  Remaining. "After the checkpoint" compares the payment's HLC with the checkpoint's HLC, not
  wall-clock `PaidAt`. A payment that was unmarked and marked again orders by the HLC of its
  latest mark.
- **Payments merge as a set.** Each payment is its own row, so concurrent payments all survive.
  The regular "mark paid" payment for a bill in a period gets a **deterministic ID** derived from
  (bill ID, period key), so two devices marking the same bill paid in the same period produce one
  payment, not two. Extra or one-time payments (a §9 feature candidate) get random IDs and simply
  add up. Unmark sets the deterministic payment's deleted marker, which is an ordinary
  last-write-wins field: marking the same bill paid again in that period clears it.
- Other payment fields edited on two devices resolve per field like bills.
- Merge must be deterministic: the same set of changes in any order produces the same database on
  every platform. This is the core of the conformance suite.

### 6.3 What goes in the drive

```
<app folder>/PayDay/
├── vault.json                # format version, key generations (id + check value), created-at; no secrets
├── devices/
│   └── <deviceId>/
│       ├── 000001.chg        # encrypted batch of changes, append-only
│       └── 000002.chg
└── snapshots/
    └── <hlc>-<deviceId>.snap # encrypted full state, written periodically for fast first sync
```

- Each device only ever writes its own folder, so two devices never fight over the same file.
- A device reads everyone else's new files, merges, and records how far it has read.
- Compaction: any device may write a snapshot; files older than the newest snapshot that all
  known devices have passed can be removed. Thresholds are left to the builder.
- Drive locations: OneDrive app folder (`approot`), Google Drive `appDataFolder`, Dropbox App
  folder. These count toward the user's own storage; the data is tiny.

### 6.4 Encryption and keys

- One random 256-bit **vault key**, created on the first device.
- Files encrypted with **AES-256-GCM** (Claude's call: it's built into .NET, CryptoKit and
  Android's crypto APIs, so no third-party crypto library on any platform). Fresh random nonce per
  file. Exact framing (header, nonce, ciphertext, tag) is fixed in `spec/sync-protocol.md` with
  test vectors in `spec/conformance/`.
- The vault key lives in the OS keystore on each device (Windows Credential Manager — the same
  store the Notion token uses today; Apple Keychain; Android Keystore). It is never written to the
  drive.
- **Key generations (Claude's call):** every file header names the key generation it was
  encrypted with, and `vault.json` lists generations. v1 ships with one generation and no
  "remove device" screen, but the format supports rotating to a new key later (cutting off a lost
  or stolen device, which also needs signing that device out of the drive provider). Rotation
  issues a new recovery phrase.
- **Recovery phrase:** the vault key is shown once as a word list. Lose every device and the
  phrase, and the synced data cannot be recovered by anyone (Claude's call: this is the price of
  "nobody else can read it"; the onboarding screen says so plainly and offers a local JSON export
  as a second safety net).

### 6.5 Setup flows

First device:

1. "Turn on sync" → pick OneDrive / Google Drive / Dropbox → that provider's sign-in page.
2. App creates the vault, uploads the current data, shows the recovery phrase ("Save this").

Next device:

1. "I already use PayDay" → sign in to the same provider.
2. Unlock with either a **QR code** shown on an existing device (phones scan it; Macs can use the
   camera) or by **typing the recovery phrase** (Windows PCs without a camera).
3. Data appears.

A device that already has its own data joining an existing vault **(Claude's call)**: v1 does not
merge two independent databases. The app saves the device's current data as a JSON backup file,
then replaces it with the vault's data, and says so before doing it. (Merging two histories that
were never synced would match bills by name and guess at payments — not worth the risk of silent
errors.)

Existing installs (the developer's): one device's database becomes the first device's data;
nothing is re-entered. The others join as above. Which device holds the real data is the open
question in §11.

**Restoring a JSON backup with sync on (Claude's call):** restore means "make every device look
like this backup." It is written as new edits (every restored field re-stamped with a fresh HLC,
every row missing from the backup tombstoned), so it propagates everywhere and is not undone by
the next sync. The app confirms first and saves the current state as a backup. Per-device settings
in the backup are applied only to the device doing the restore.

### 6.6 Provider order

**Google Drive first (changed 2026-11-25):** it works on Windows, Mac, iOS, Android and the web,
and its OAuth client needs only a free Google Cloud project, while a OneDrive app ID now needs an
Entra tenant. OneDrive is built too (the first plan put it first) and is offered wherever a build
has its app ID. Dropbox third. iCloud (CloudKit) only as a later convenience for Apple-only users —
it can't reach Windows or Android.

### 6.7 The web app as a device

- The browser is one more device: its own local database, its own folder in the vault, the same
  merge and encryption rules. Vercel serves only the app's files and never sees data.
- Local database: SQLite compiled to WebAssembly, stored in the browser's private file storage
  (OPFS). Encryption: WebCrypto AES-256-GCM, matching the other apps' framing byte for byte.
- Vault key: kept as a non-extractable WebCrypto key in IndexedDB (browsers have no OS keystore).
- Drive sign-in happens in the browser with PKCE, which needs no secret: Microsoft, Google and
  Dropbox all support it for browser apps.
- **Sync is required for real use on the web (Claude's call).** Because the browser can erase its
  storage, the drive holds the real copy and the browser is a working copy. If the browser is
  wiped, the user signs in and unlocks again (QR or recovery phrase) and the data comes back. The
  app also asks the browser for persistent storage and suggests installing it as an app.
- **Try-it mode:** without signing in, the web app opens with clearly labelled sample data, so
  someone can see what it does before installing anything or connecting a drive.
- The same site hosts the product's landing page, privacy policy and terms (needed by the app
  stores and the Notion integration).

## 7. Notion connector

- Notion stops being how devices sync. It becomes an optional connector: PayDay's data flows to
  Notion and Notion edits to bills flow back in as ordinary changes.
- **Connection flow:** "Connect Notion" → Notion's consent page → user picks a page → PayDay
  creates the Bills, Payments and Snapshots databases under that page with the right columns.
  No token pasting, no column renames.
- **Notion OAuth exchange:** a Vercel Function in the web app's project (`apps/web`), holding the
  client secret (Claude's call: Vercel is already paid for and hosts the web app, so there's one
  vendor instead of adding Cloudflare). It exchanges the authorization code for tokens and refreshes
  them. It stores nothing. Native apps receive the result through a custom URL scheme (protocol
  activation on Windows, URL scheme on Apple, intent filter on Android); the scheme name follows
  the product name. The web app receives it on a normal redirect page.
- Notion page IDs are stored in the synced data, so any device can run the Notion connector without
  creating duplicates. The Notion token itself is per-device. Which device runs it, and how two
  connected devices avoid double-pushing, is left to the builder (simplest acceptable answer: the
  device that connected Notion owns the connector, and others show "Notion: connected on <device>").
- Existing workspace: the developer's current three databases are adopted rather than recreated
  (the connector can be pointed at existing databases).
- A Notion public integration needs a privacy policy and terms page; those are needed for the app
  stores anyway.

## 8. Shared conformance suite

- `spec/conformance/` holds JSON fixtures: input → expected output. Every app's test project loads
  the same files.
- First fixtures are generated from the **current C# behaviour**, which is the reference
  implementation:
  - pay periods (anchor, 14-day walk, This/Next/Following, the 3 PM early start);
  - bill-to-period assignment, totals, subtotals excluding inactive bills;
  - payoff ordering (time-to-payoff, snowball, avalanche) and payoff dates;
  - merge scenarios (concurrent field edits, delete vs edit, duplicate mark-paid, out-of-order
    delivery);
  - encryption test vectors and file framing;
  - backup JSON round-trip, including legacy `cost`/`owed` keys.
- A rule or format change lands in `spec/` and its fixtures first, then in each app.

## 9. Build order

1. **Restructure the repo** to the §5 layout. No behaviour change. Bring `main` up to the
   `wip/backup-2026-09-09` tip first (done 2026-11-14), and work on a `feature/` branch.
2. **Lock in the product name: FutureCheck (decided, 2026-11-14).** "PayDay" was crowded in the
   stores, suggested payday loans, and is a video-game franchise. "Check" means both the paycheck
   and the checkmark on a paid bill; "Future" points at the forecast. A web search on 2026-11-14
   found no app named FutureCheck on the App Store, Google Play or Microsoft Store. Still to do:
   - US trademark: the user searched USPTO tmsearch on 2026-11-14. "FutureCheck" returned no
     results; "Future Check" returned only marks containing one of the two words, none for the
     phrase. That's a clean first pass, not a legal clearance; a trademark attorney's knockout
     search is worth doing before spending on marketing.
   - The user registers `futurecheck.app` (available 2026-11-14) through their existing registrar,
     Squarespace, and optionally `futurecheck.money` to protect the name. `futurecheck.com` is
     taken. DNS then points the domain at the web app's Vercel project.
   - Use the name everywhere it's needed: the Notion public integration, the custom URL scheme
     (`futurecheck://`), bundle and package IDs (`dev.quantumink.futurecheck`, Claude's call), and
     the store listings. Until the domain is live, `payday.quantumink.dev` serves for testing. The
     code keeps the PayDay name internally until a rename pass (left to the builder to schedule).
     The Windows MSIX package identity (`Identity Name` in `Package.appxmanifest`) is NOT changed by the
     rename pass, because changing it orphans the installed app's LocalState data. The bundle and package
     IDs `dev.quantumink.futurecheck` apply to the new Apple and Android apps, and any Windows identity
     change needs a data migration first.
3. **Scope new features** in their own short design (shortlist below). This runs before the sync
   format is frozen (Claude's call), because features like extra payments, income tracking and
   set-asides add synced data. Deciding them first avoids migrating a live format across devices.
4. **Write `spec/` and the first conformance fixtures** from the C# core plus the chosen features'
   data; wire them into `PayDay.Tests`.
5. **Sync in the Windows app:** ID migration, balance checkpoints, HLC + tombstones, change files,
   encryption, Google Drive and OneDrive, setup and pairing screens. Two Windows installs (VM + rig) syncing is the
   exit test.
6. **Notion connector rework** (OAuth function on Vercel, auto-created databases, adopt existing
   ones).
7. **macOS app** (SwiftUI, `PayDayKit`) at feature parity with Windows, passing the conformance
   suite, syncing with Windows.
8. **Web app** (TypeScript, static on Vercel), passing the conformance suite, with try-it mode and
   the landing, privacy and terms pages (Claude's call to place it after the Mac app).
9. **iOS app** from the same `PayDayKit`.
10. **Android app** (Kotlin, Jetpack Compose, SQLite), passing the conformance suite.
11. **Google Drive, then Dropbox** providers.
12. **Phase 8 — ship:** store listings, signing, MSIX/App Store/Play.

The chosen features are built on Windows in or after step 5 and are part of the parity target for
the Mac and web apps. Research shortlist (2026-11-14), strongest first: due-date reminders with "mark paid"
from the notification or widget; a forecast of what's left across upcoming paychecks and a
safe-to-spend number; set-asides for yearly or irregular bills; income tracking, including
irregular pay and two incomes; extra/one-time debt payments and target-date payoff; payment
confirmation numbers plus CSV/PDF export; sharing a plan with a partner.

## 10. Left to the builder

- Web framework (Vite or a Next.js static export) and the SQLite-in-browser library.
- Platform minimums (macOS/iOS/Android versions), Swift persistence library (GRDB or raw SQLite;
  not SwiftData, to keep the schema identical to Windows), Android persistence (Room or SQLDelight).
- Exact HLC encoding, file framing, compaction thresholds, sync triggers (on launch, on change with
  debounce, periodic while open).
- Recovery-phrase word list and length.
- How sync status and errors are shown in each app.
- Whether the Windows Notion code is refactored into the new connector or rewritten.

## 11. Seeding the vault from the developer's installs

The code is built on the rig and ported from there; the arm64 VM is where the app is tested and
used, and probably has the most recent **data** (user, 2026-11-14 — not certain). So:

- Before sync is turned on (step 5), export a JSON backup from **both** the VM and the rig and
  compare them: newest `UpdatedAt` per bill, payment counts and latest `PaidAt`, latest snapshot.
  A small comparison script does this; the user picks the seed from its report.
- Expected outcome: the VM seeds the vault. The rig then joins as a second device (its data saved
  to a backup file first, per §6.5).
- If the comparison shows edits on both sides that matter, they are reconciled by hand into the
  seed device's data **before** sync starts — v1 has no automatic merge of independent histories.
- Both backups are kept (outside the repo — they contain real financial data).

No open questions remain.
