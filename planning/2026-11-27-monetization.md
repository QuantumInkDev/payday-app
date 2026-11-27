# FutureCheck monetization: free trial, then a one-time unlock

**Date:** 2026-11-27 · **Status:** the model is the developer's direction. The mechanism is Claude's
proposal. Prices and store rules wait on the research bot's report, which will be added here.

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
| Microsoft Store | Durable add-on through Store commerce, or our own (research to confirm). |
| Windows direct download, web app | A merchant of record such as Lemon Squeezy, Paddle or Polar sells a license key and handles sales tax and VAT. The app activates the key against the merchant's public license API directly, so there's no server of ours. |

### How strict to be

- The vault belongs to the user, so a determined user could hand-write an entitlement record.
- At this scale, accept that: honest-user licensing. Don't build or run a license server.
- Store receipts are checked on the device by each store's SDK, and web keys are checked by the
  merchant's API at activation.
- The vault only carries the result.

### The trial

- **Length:** full use for **2 pay periods, and at least 30 days**. The minimum keeps weekly-paid
  users from getting only two weeks.
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

## Open, pending research

- **Launch and regular price:** is $10 → $20 reasonable, and should the launch price use "intro
  price" framing?
- **Store rules:** Apple 3.1.1 / 3.1.3(b) multiplatform services (honouring a vault entitlement
  bought elsewhere), Google Play's equivalent, and whether Microsoft Store allows its own commerce.
  Also each store's fees and small-business programs.
- **Merchant-of-record choice** and its license-key API.

## Where it fits

- The format first: the entitlement record and the trial start setting go into `spec/` with the
  pay-schedule work, so the sync format changes once.
- Then the Windows unlock flow.
- Store listings come at Phase 8 (ship).
