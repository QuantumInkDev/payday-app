# FutureCheck monetization: free trial, then a one-time unlock

**Date:** 2026-11-27 · **Status: decided** (developer, 2026-11-27):
- **Price:** $9.99 launch price, then $19.99.
- **Trial:** 30 days, full features. After it, viewing and export stay available.
- **Unlock:** one unlock for every device in the vault.
- **Selling:** Polar for Windows and web sales; no license-key entry in App Store builds.

Research summary and sources are at the end.

## Direction (developer)

- People can use it fully at first to see if they like it: roughly the first **2 pay periods**.
- Then a **one-time unlock**, not a subscription.
- Price: about **$10** introductory, moving toward **$20** if it takes off.
- Ask: should the unlock be per device, or one for all devices? It needs a durable way to deliver the
  unlock that is reasonable at our scale.

## Proposal

### One unlock covers every device in your vault

The vault, the encrypted synced data, already serves as the user's identity across devices, and
there are no FutureCheck accounts. So:

- Buying on any platform unlocks every device that syncs the same vault.
- A device that doesn't sync unlocks the usual way for its store ("Restore purchases"), or with a
  license key.

Per-device pricing would feel punitive for a product whose whole pitch is following you across
devices. It would also need its own per-device bookkeeping.

### How the unlock is recorded

The vault gets one more synced record type, an **entitlement**:

| Field | Meaning |
|---|---|
| `source` | `msstore`, `appstore`, `play` or `web` |
| `proof` | the store transaction ID, or the license key |
| `product` | `unlock-v1` |
| `unlockedAt` | when it was bought or activated |

- Every device treats the vault as unlocked if any live entitlement exists.
- It merges like any other record, by deterministic ID from (source, proof), so buying twice or
  activating on two devices gives one row.

### Where people buy

| Platform | How |
|---|---|
| iOS / macOS App Store | Non-consumable in-app purchase (required by store rules). Restore purchases works across the user's Apple devices. |
| Google Play | Play Billing one-time product (required by store rules). |
| Microsoft Store | Own commerce: Polar, with a 0% Microsoft commission for non-game apps. Store commerce would cost 15%. |
| Windows direct download, web app | **Polar** as merchant of record (5% + $0.50, plus international and payout fees). It sells a license key and handles sales tax and VAT. The app activates the key against Polar's public activation and validation endpoints, which need no merchant secret, so there's no server of ours. |

### How strict to be

- The vault belongs to the user, so a determined user could hand-write an entitlement record.
- At this scale, accept that: honest-user licensing. Don't build or run a license server.
- Store receipts are checked on the device by each store's SDK, and web keys are checked by the
  merchant's API at activation.
- The vault only carries the result.

### The trial

- **Length: 30 days, full features, no card and no auto-billing** (recommended). Research found that
  "two pay periods" is misleading once schedules vary: two weeks for a weekly-paid user, two months
  for a monthly one. Thirty days is about two biweekly paychecks. YNAB uses 34 days.
- **Tracking:** the trial start date is stored in the vault, so adding a device doesn't restart it.
  Reinstalling without sync does restart it, which is acceptable.
- **After the trial, never hold data hostage.** Viewing, export and backups stay free, and sync
  keeps running. Adding or editing bills and marking bills paid need the unlock. This also fits the
  privacy promise.
- **Messaging:** a quiet banner near the end of the trial. No nags during it.

### Site and store copy

- No "free" claims until this ships.
- The site's pricing element is tagged `data-roadmap="pricing"`.
- Store listings have to match whatever each store's trial rules allow.

## Research findings (Ray, 2026-11-27)

### Price

- **Recommendation: $9.99 launch price, then $19.99 regular.** Call it a "launch price" with a
  genuine end date, and never show an invented crossed-out price.
- **One-time apps:** Paycheck Planner $4.99, Money Manager $5.99, Buckets $64. Actual Budget is
  free.
- **Subscriptions:** YNAB $109 a year, Monarch $99.99 a year, Copilot $109–119 a year, PocketGuard
  $74.99 a year or $149.99 lifetime, Goodbudget $80 a year.
- **Not one-time:** MoneyWiz and Banktivity are subscriptions now.
- **The economic risk** is maintaining and supporting five platforms, not hosting.

### Trial

- 30 days, full features.
- Afterwards, read-only access and export are preserved indefinitely. Banktivity is a precedent;
  users resent having editing taken away when it wasn't the promise.

### Store rules (Apple guidelines of 2026-10-16)

- **Apple 3.1.3(b):** features bought elsewhere may be unlocked if the same unlock is also sold as
  in-app purchase.
- **Apple 3.1.1** forbids unlocking with license keys inside the app. So App Store builds have no
  key-entry screen.
  - Before promising one unlock across platforms, explain to App Review that the unlock arrives
    through the vault, and get it confirmed.
  - Apple explicitly allows a separate free non-consumable "30-day trial" item, and a paid
    non-consumable unlock. Include Restore Purchases.
- **Google Play:** honouring purchases made elsewhere is fine. Selling in the app needs Play Billing,
  except under regional programs.
- **Fees:**
  - Apple: 15% under the Small Business Program (≤ $1M), otherwise 30%.
  - Play: 15% on the first $1M.
  - Microsoft, non-game apps: 0% with own commerce, or 15% through Store commerce.

### Selling outside the stores

| Option | Verdict |
|---|---|
| **Polar** | **Pick.** 5% + $0.50, merchant of record, public license activate/validate endpoints. |
| **Lemon Squeezy** | **Out.** Sign-ups closed 2026-11-16 and payments stop 2027-03-31. |
| **Paddle** | No built-in license keys; would need a backend. |
| **Gumroad** | 10% + $0.50 plus card fees. |
| **Stripe** | Not a merchant of record, so we'd file sales taxes ourselves. Managed Payments costs another 3.5%. |

### Engineering consequences

- **The vault carries the evidence, not the verdict.** An entitlement keeps verifiable proof (the
  Polar key, or the store transaction), never just `paid = true`.
- **No merchant secret** is ever embedded in the app.
- **Offline use:** cache the unlock so ownership doesn't depend on the vendor staying online
  forever.
- **Copying and refunds:** encryption doesn't stop someone copying a license, and without a backend
  we accept weak anti-sharing and refund enforcement.
- **Privacy policy:** it has to name the payment provider separately from financial-data sync.

Sources: Ray's report in the T3 thread (store guidelines, pricing pages and merchant fee pages,
all accessed 2026-11-27).

## Where it fits

- The format first: the entitlement record and the trial start setting go into `spec/` with the
  pay-schedule work, so the sync format changes once.
- Then the Windows unlock flow.
- Store listings come at Phase 8 (ship).
