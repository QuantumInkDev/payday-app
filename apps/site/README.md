# @futurecheck/site

The FutureCheck marketing website: Astro (static output, TypeScript strict) with Tailwind CSS v4
through `@tailwindcss/vite`, built to `dist/`. Pages ship no client JavaScript; anything interactive
later is a small Astro island or inline script on the page that needs it.

Routes: `/` (landing), `/privacy` (rendered from the repo-root `PRIVACY.md` at build time),
`/terms` (placeholder, noindex, not in the sitemap), a 404 page, `/sitemap-index.xml` (from
`@astrojs/sitemap`) and `/robots.txt`.

The web app is a separate workspace (`apps/web`), not part of this site.

## Run it

Always install from the repository root, never from `apps/site`:

```sh
pnpm install          # at the repo root
pnpm dev:site         # http://localhost:4321
pnpm build:site       # static site in apps/site/dist
pnpm lint             # ESLint (eslint-plugin-astro + typescript-eslint), fails on any warning
pnpm typecheck        # astro check, fails on any warning
```

To preview the static build, run `pnpm --filter @futurecheck/site preview`, or serve
`apps/site/dist` with any static file server.

## Where things live

- `src/site.config.ts`: site name, tagline, base URL, nav and footer links, and the pages kept out
  of the sitemap. The domain appears nowhere else. `astro.config.ts` passes the base URL to Astro
  as `site`.
- `src/pages/`: routes, plus `robots.txt.ts` (prerendered to `robots.txt`).
- `src/components/BaseHead.astro`: the one shared `<head>`: title, description, canonical URL,
  robots, Open Graph and Twitter tags. Pages set them through `src/layouts/BaseLayout.astro`.
- `src/pages/privacy.astro` imports `../../PRIVACY.md` (the repo root) as a Markdown module. That
  file stays the only source of the privacy policy; edit it there, not here.
- Brand tokens come from `packages/brand/tokens.css` (`@futurecheck/brand`) and are mapped to
  Tailwind utilities in `src/styles/global.css` (`@theme inline`). The designer fills them from
  `design/site/DESIGN.md`.

## Base URL

| Variable | Purpose |
|---|---|
| `SITE_URL` | Canonical origin, for example `https://futurecheck.app`. Used for canonical links, Open Graph URLs, the sitemap and robots.txt. |
| `PUBLIC_SITE_URL` | Same, used only when `SITE_URL` is not set. |

If neither is set, a Vercel build falls back to `VERCEL_PROJECT_PRODUCTION_URL`, then `VERCEL_URL`
(both set by Vercel), and a local build uses `http://localhost:4321`. A bare host is accepted and
gets `https://`. The values are read from the build environment (`process.env`, not `.env` files)
and baked in at build time.

Vercel sets `VERCEL_PROJECT_PRODUCTION_URL` on preview builds too, so previews point their canonical
links at the production URL. On Vercel preview and development deployments (`VERCEL_ENV` other than
`production`), robots.txt is `Disallow: /` and every page is marked `noindex, nofollow`.

These variables are declared in `apps/site/turbo.json`, so Turborepo passes them to the build and
includes them in the cache key. Add any new build-time variable there too.

## Deploy (Vercel)

Not set up yet. When it is:

1. Create a Vercel project on the Quantum Ink team from this repository.
2. **Root Directory:** `apps/site`. Keep "Include files outside the root directory" on, because the
   build reads `PRIVACY.md` and `packages/brand` from the repo root.
3. **Framework Preset:** Astro (Vercel usually detects it). The defaults fit: build command
   `astro build`, output directory `dist`. No adapter is needed for a static site.
4. **Ignored Build Step:** `npx turbo-ignore`. It skips the build when a commit doesn't touch the
   site or its workspace dependencies (`packages/brand`), so changes to the native apps don't
   rebuild the site.
5. **Environment variables:**
   - `SITE_URL` for Production once the domain is attached. Leave it unset for Preview.
   - `ENABLE_EXPERIMENTAL_COREPACK=1` (all environments), so Vercel installs with the pnpm version
     pinned in the root `package.json` (`packageManager`) instead of guessing from the lockfile.
