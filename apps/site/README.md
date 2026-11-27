# @futurecheck/site

The FutureCheck marketing website: Next.js (App Router, TypeScript strict) with Tailwind CSS v4,
built as a static export to `out/`.

Routes: `/` (landing), `/privacy` (rendered from the repo-root `PRIVACY.md` at build time),
`/terms` (placeholder, noindex), a 404 page, `/sitemap.xml` and `/robots.txt`.

## Run it

Always install from the repository root, never from `apps/site`:

```sh
pnpm install          # at the repo root
pnpm dev:site         # http://localhost:3000
pnpm build:site       # static export in apps/site/out
pnpm lint             # ESLint, fails on any warning
pnpm typecheck        # next typegen + tsc --noEmit
```

To preview the static build, serve `apps/site/out` with any static file server.

## Where things live

- `src/site.config.ts`: site name, tagline, base URL, nav and footer links. The domain appears
  nowhere else.
- `src/app/`: routes, plus `sitemap.ts` and `robots.ts` (Next.js metadata APIs).
- `src/lib/privacy-policy.ts`: reads `../../PRIVACY.md`. That file stays the only source of the
  privacy policy; edit it there, not here.
- Brand tokens come from `packages/brand/tokens.css` (`@futurecheck/brand`) and are mapped to
  Tailwind utilities in `src/app/globals.css`. The designer fills them from `design/site/DESIGN.md`.

## Base URL

| Variable | Purpose |
|---|---|
| `NEXT_PUBLIC_SITE_URL` | Canonical origin, for example `https://futurecheck.app`. Used for canonical links, Open Graph URLs, the sitemap and robots.txt. |

If it isn't set, a Vercel build falls back to `VERCEL_PROJECT_PRODUCTION_URL`, then `VERCEL_URL`
(both set by Vercel), and a local build uses `http://localhost:3000`. A bare host is accepted and
gets `https://`. Because the site is a static export, the value is baked in at build time.

On Vercel preview and development deployments (`VERCEL_ENV` other than `production`), robots.txt
disallows everything and pages are marked noindex.

These variables are declared in `apps/site/turbo.json`, so Turborepo passes them to the build and
includes them in the cache key. Add any new build-time variable there too.

## Deploy (Vercel)

Not set up yet. When it is:

1. Create a Vercel project on the Quantum Ink team from this repository.
2. **Root Directory:** `apps/site`. Keep "Include files outside the root directory" on, because the
   build reads `PRIVACY.md` and `packages/brand` from the repo root. Vercel detects the Next.js
   framework and pnpm.
3. **Ignored Build Step:** `npx turbo-ignore`. It skips the build when a commit doesn't touch the
   site or its workspace dependencies (`packages/brand`), so changes to the native apps don't
   rebuild the site.
4. **Environment variables:**
   - `NEXT_PUBLIC_SITE_URL` for Production once the domain is attached. Leave it unset for Preview
     so previews use their own URL.
   - `ENABLE_EXPERIMENTAL_COREPACK=1` (all environments), so Vercel installs with the pnpm version
     pinned in the root `package.json` (`packageManager`) instead of guessing from the lockfile.
