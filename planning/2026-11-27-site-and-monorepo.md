# FutureCheck website and the Turborepo monorepo

**Date:** 2026-11-27. **Status:** decided; the site is being built on `feature/site`.

## Why now

To go "In production", the Google sign-in consent screen needs a homepage URL and a privacy-policy
URL. For now they point at the GitHub repo and [`PRIVACY.md`](../PRIVACY.md). This site replaces
both with real pages (home, privacy, terms). The consent screen and the store listings will link to
it.

## Repository layout (changes design §5)

The developer asked whether Turborepo fits, and to use the ideal Turborepo setup if it does.

**It fits, as one repository.** Turborepo works best with one repo and one pnpm workspace. Git
submodules work against it: each one is pinned to its own commit, so Turborepo can't hash or cache
across them, and you lose single commits that change the spec and every app together. So the
"sub-repos" are folders in this repo:

```
PayDayApp/  (QuantumInkDev/payday-app; may be renamed futurecheck later)
├── apps/
│   ├── site/       Next.js marketing site, deployed on Vercel  ← new
│   ├── web/        later: the web app (design step 8)
│   ├── windows/    .NET 9 / WinUI 3 (unchanged)
│   ├── apple/      Xcode project + PayDayKit (unchanged)
│   └── android/    later
├── packages/       shared JavaScript: brand tokens and UI used by site and web; later a TypeScript spec runner
├── spec/           cross-platform contract and conformance fixtures (unchanged)
├── package.json, pnpm-workspace.yaml, turbo.json
```

- **JavaScript workspaces:** `apps/site`, `apps/web` and `packages/*`. Run the site with
  `pnpm dev:site` from the root, and always run `pnpm install` from the root.
- **Native apps** keep `dotnet`, `xcodebuild` and Gradle. Later, each can get a small
  `package.json` with `build`/`test` scripts that call its own tool. Then `turbo run test` covers
  every surface, and Turborepo's caching skips a surface whose inputs didn't change. That step is
  optional and comes after the sync exit test.
- **Deploying:** a Vercel project with Root Directory `apps/site`, using `turbo-ignore`, so commits
  that only touch other surfaces don't rebuild the site.
- **Separate repos** later, if ever: `git subtree split` can pull any folder out with its history.

## The site

- **Pages:**
  - Home: the promotional landing page.
  - Privacy: from `PRIVACY.md`, which stays the source of truth.
  - Terms.
  - Later: support/FAQ and download links for each store.
- **Domain:** `futurecheck.app` is planned (design §9 step 2). The developer registers it through
  Squarespace when ready, so it isn't bought yet. Until then the site runs on its Vercel URL or
  `payday.quantumink.dev`.
- **Hosting:** the Quantum Ink team's paid Vercel plan, so commercial use is covered.
- **Product facts:** from `CLAUDE.md`, design §1–2 and `PRIVACY.md`.
  - A personal finance tracker built around biweekly pay periods: bills, credit cards, loans,
    subscriptions and installment plans, with a dashboard, insights charts and a payoff tracker
    (snowball/avalanche).
  - Local-first, with no accounts and no servers.
  - Optional sync through the user's own Google Drive (or OneDrive), end-to-end encrypted with a
    recovery phrase.
  - Optional Notion connector.
  - Windows now; macOS, iOS, Android and web planned. Free to run.
  - **Not** a bank-linking app, and it doesn't import transactions.
- **Public repo rules apply** to everything committed here: no real financial figures, no
  institution or brand names from the developer's data, no real pay dates, and no secrets or client
  IDs. Screenshots use sample data only.

## Who's building it

These are Hermes bots, coordinated from this T3 thread:

- **designer (Mai):** art direction, brand system, page layouts.
- **studio (Ermac):** logo and wordmark, hero and feature imagery, OG images.
- **dev (Shang Tsung):** Next.js build in `apps/site`.
- **publisher (Dracula):** SEO, metadata, OG/Twitter cards, analytics choice, launch checklist,
  store-listing copy hooks.

Final review is by Claude in this thread; the developer approves.
