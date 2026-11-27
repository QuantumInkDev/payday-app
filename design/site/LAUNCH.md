# FutureCheck site: launch and discoverability plan

From the publisher bot (Dracula), 2026-11-27. These are recommendations, not decisions.

## 1. SEO

Title template: `%s | FutureCheck`. Set `metadataBase` from one `SITE_URL` env var, and put a
canonical on every page.

| Page | Title | Description |
|---|---|---|
| Home | FutureCheck: Pay-Period Budgeting, Private by Design | Plan every bill around your biweekly paycheck. No FutureCheck account and no bank linking. Your data stays on your device unless you turn on sync, which is end-to-end encrypted through your own cloud drive. |
| Privacy | Privacy Policy \| FutureCheck | FutureCheck stores your data on your device. Optional drive sync is encrypted before upload; the optional Notion connector is not encrypted by FutureCheck. No analytics or ads. |
| Terms | Terms of Service \| FutureCheck | Terms for using FutureCheck, a local-first personal finance tracker: what the app does, what you're responsible for, and what we don't promise. (Draft: keep noindex until the Terms are approved.) |

**Keywords.** The privacy cluster (offline, no bank linking, private) fits better than the crowded
biweekly-budget cluster.

- **Primary:** biweekly budget app, pay period budget app, budget app without bank linking.
- **Secondary:** paycheck budget planner, private offline budget app, bill tracker by paycheck,
  debt snowball avalanche app, budget app no account.
- Put "pay period" and "private" in the Home H1.
- Don't mention reminders or safe-to-spend until they ship.

**Structured data** (JSON-LD on Home):

- `SoftwareApplication`, with:
  - `applicationCategory: FinanceApplication`
  - `operatingSystem: "Windows 10, Windows 11"` (add others only when they ship)
  - `offers` price 0 USD (confirm the app is free)
  - one screenshot with sample data
- Also `WebSite` and `Organization`.
- No ratings or reviews.
- Ship `sitemap.xml` and `robots.txt`.
- Mark preview and test hosts noindex, or 308-redirect them to the apex after cutover.

## 2. Open Graph and Twitter

**Fields:**

- `og:title`, `og:description`
- `og:type=website`, `og:url`, `og:site_name=FutureCheck`, `og:locale=en_US`
- `og:image` (absolute URL), with `og:image:width=1200`, `og:image:height=630` and `og:image:alt`
- `twitter:card=summary_large_image`, `twitter:title`, `twitter:description`, `twitter:image`,
  `twitter:image:alt`
- Add `twitter:site` only once a handle exists.

**Image:**

- 1200×630 PNG or JPG, under 300 KB.
- **Authoritative export spec (reconciled with DESIGN.md):** keep the logo and all text inside the central 1000×500, with text at least 56 px. This is stricter than, and fits inside, DESIGN.md's 1072×502 / 32 px floor.
- Sample data only.
- Keep a provenance note for any AI-generated art.

## 3. Analytics

**Recommendation: none at launch.** That keeps PRIVACY.md's "no analytics" literally true. Measure
with Search Console, and later the store consoles. Self-host fonts with `next/font`, and use no
third-party embeds.

If traffic counts are wanted later, Vercel Web Analytics (cookieless), Plausible or Umami would
work. Any of them means adding a "This website" section to PRIVACY.md and bumping its date. That's
Quantum's call.

## 4. Launch checklist (once futurecheck.app is registered)

1. Set `SITE_URL` in Vercel Production. Lower the DNS TTL a day ahead.
2. In Vercel → Domains, add `futurecheck.app` as primary, and `www` redirecting to the apex.
3. In Squarespace DNS:
   1. Remove the parking records.
   2. Add the A record for `@` and the CNAME for `www`, using the exact values Vercel shows.
4. Wait for Valid Configuration and SSL.
   1. Test both hosts over https.
   2. Redeploy, then check the canonical URLs, OG URLs and sitemap.
   3. 308-redirect the old hosts.
5. Search Console:
   1. Add a Domain property and verify it with a DNS TXT record. The record goes in DNS, never in
      the repo.
   2. Submit the sitemap.
6. Google OAuth Branding, only after the site is live:
   1. Set the homepage, privacy and terms URLs to the new domain.
   2. Authorize `futurecheck.app`. This needs Search Console ownership.
   3. Don't change scopes.
   4. Keep the GitHub URLs live until Google confirms.
   5. Then update the Notion integration URLs.

## 5. Store-listing hooks

- **Name:** FutureCheck
- **App Store subtitle** (30 characters max): Budget by pay period
- **Play short description** (80 characters max): Plan every bill around your paycheck. No
  FutureCheck account, no bank linking.
- **Value props:**
  - Every bill in the paycheck that covers it: biweekly pay periods, a dashboard, insights, and
    snowball or avalanche payoff.
  - Private by design: no FutureCheck account, no bank linking, and your data stays on your device
    unless you turn on sync.
  - Sync only if you choose: end-to-end encrypted through your own Google Drive or OneDrive, with a
    recovery phrase. (The optional Notion connector is separate and not encrypted by FutureCheck.)
- **Cautions:**
  - List only features that have shipped.
  - Match the Apple privacy label and the Play Data safety form to PRIVACY.md.
  - No "bank-grade" or "unhackable" claims.
