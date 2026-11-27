---
version: alpha
name: FutureCheck — Open Relay
description: Paycheck clarity with a bold petrol entrance, glacier fields, and a warm human handoff.
colors:
  primary: "#073e43"
  secondary: "#27636b"
  accent: "#ff9255"
  canvas: "#d7eef0"
  surface: "#ffffff"
  warm: "#f7f6f2"
typography:
  h1:
    fontFamily: Chakra Petch
    fontSize: 3.0625rem
    fontWeight: 600
    lineHeight: 1.05
    letterSpacing: "-0.04em"
  body-md:
    fontFamily: IBM Plex Sans
    fontSize: 1.125rem
    fontWeight: 400
    lineHeight: 1.6
rounded:
  sm: 9px
  md: 12px
  lg: 24px
spacing:
  unit: 4px
  gap: 24px
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.primary}"
    rounded: "{rounded.sm}"
    padding: 13px
  body:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.primary}"
  secondary-text:
    backgroundColor: "{colors.warm}"
    textColor: "{colors.secondary}"
  hero:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.canvas}"
---

# FutureCheck Website Design System

Status: Open Relay v3 is Quantum's selected direction. These revisions are ready for Claude's review, not production or launch approval.
Scope: promotional Home, Privacy, Terms, shared brand tokens, and a studio asset brief. No application redesign, release, pricing decision, domain operation, commit, push, or deployment.
Coordination: T3 thread `0411876b-02ba-457a-ab44-db4a4b326d7b`.
Visual reference: `design/site/v3-open-relay.html`, a self-contained interactive design illustration, not the implemented site or a product screenshot.

## Overview

### Direction and rationale

Open Relay combines Open Circuit's recognizable side rail, Chakra/Plex typography, glacier/petrol palette, and apricot action with Payday Relay's plain, confident voice. The subject is the handoff from this paycheck to the next, not a network diagram, bank, investment dashboard, or money-transfer service.

The entrance is a broad petrol field with a large, left-aligned “Put bills in paycheck order.” The page then becomes quieter: a white sample list, warm-white privacy section, and friendly on-device composition. Curved connections repeat as the one graphic motif. Warmth comes from language, clear spacing, rounded paths, and everyday objects—not beige, mascots, or decorative fintech imagery.

| Direction considered | Decision and reason |
| --- | --- |
| Forecast Ledger | Superseded. Quantum rejected the editorial serif/violet direction as generic and AI-looking. Do not bring its tokens or logo construction forward. |
| Payday Relay alone | Its copy and switchable paycheck sample carry forward. Its full cream palette and Barlow typography do not: Quantum preferred Open Circuit's visual identity. |
| Open Circuit alone | Keep the identity, but soften architecture cues, dense labels, and console-like structure. Personal finance should feel approachable, not like infrastructure software. |
| Pocket Logic / envelope metaphor | Not selected. Envelopes suggest a budgeting model that is not the product's organizing idea. |
| Glowing, dark-only finance imagery | Rejected. Suggests trading, prediction, or AI; competes with practical bill tracking. |

### Product and copy guardrails

- FutureCheck tracks bills, credit cards, loans, subscriptions, and installment plans. Dashboard, insights charts, and snowball/avalanche payoff tracking are current product scope.
- Windows is current; macOS, iOS, Android, and web are planned. No invented release dates, active store badges, or unverified download buttons.
- Windows currently supports every 2 weeks only. Main headlines can say “your paychecks”; flexible schedules remain a clearly labeled preview in `data-roadmap="pay-schedules"`. The requested generic “1st & 15th” schedule definition is not a real person's pay or due date.
- Pricing is undecided. Publish no price, zero-cost claim, trial duration, or purchase promise. The status label is “For Windows” alone. If a future pricing placeholder is needed, it must be inside `data-roadmap="pricing"`, excluded from public production until approved. This study contains no pricing placeholder.
- No bank linking, transaction import, or payment execution. “Payments” in the device composition means payments the person records as paid, not money FutureCheck sends.
- Say “no FutureCheck account,” not “no sign-in.” Optional drive sync uses the person's provider account. No central FutureCheck data server does not mean the marketing site has no host or that cloud providers do not exist.
- Data starts locally. Optional drive sync encrypts financial data before upload; devices and a recovery phrase hold the key. Do not imply that the local database or exported backup files are encrypted by this diagram.
- Explain provider-visible non-secret bookkeeping and encrypted-file names/times. Notion is a separate optional connector; data sent to it is not encrypted by FutureCheck. Keep this exception visible.
- Use no real financial data, institutions from anyone's records, real pay/due dates, personal names/emails, credentials, tokens, client IDs, recovery words, or confirmation numbers. Official integration names can describe integrations; they are not sample bill names.
- No fabricated testimonials, savings claims, compliance badges, security audits, urgency, or guilt. Specific beats grandiose.

### Sources and precedence

1. Quantum's current feedback in the task governs pricing and the chosen visual direction. It supersedes the older site's planning statement about cost.
2. Root `PRIVACY.md` is the source of truth for the Privacy page, permissions, metadata disclosure, Notion exception, and removal caveats.
3. `planning/2026-11-27-site-and-monorepo.md` governs pages, stack, domain status, and team. `CLAUDE.md` “What This App Does” and `planning/2026-11-14-multiplatform-design.md` §§1–2 and 9 establish purpose, non-goals, and naming. Older feature shortlists are not evidence of availability.
4. This document and the revised `v3-open-relay.html` supersede Forecast Ledger's design contract. `0411876b-brand.css`, `0411876b-direction-study.html`, their fixture/verifier, v2 studies, and earlier screenshots are historical—not production inputs. Do not copy their violet/serif system.

### Name and logo ownership

“Check” refers to the paycheck and the checkmark on a recorded paid bill; “Future” is looking ahead to the next period. This is not payday lending or an upward-growth promise.

Studio owns the replacement logo in `design/site/logo/`. Follow that directory's reviewed source, manifest, and usage guidance when delivered; do not treat the inline v3 study mark as final artwork. No new logo construction is prescribed here, and nothing in `logo/` is edited by this revision. The website's text/display pairing is not an instruction to redraw studio's wordmark.

Until a final lockup is approved, use the exact name `FutureCheck` in live text with an accessible home link. Reserve approximately 168 × 36 px in the desktop rail, shrinking optically at the intermediate rail width. Final clear space and minimum size come from studio, not a guess based on the placeholder. No domain or platform mask is baked into the logo.

## Colors

### Palette and semantic roles

| Palette | Value | Purpose |
| --- | --- | --- |
| Petrol | `#073e43` | Primary ink, display field, selected controls, diagram paths. |
| Glacier | `#d7eef0` | Main light canvas, reverse display type, device home and sample total. |
| Apricot | `#ff9255` | Main action, small handoff accent, roadmap status. Always petrol text, never small white text. |
| Warm white | `#f7f6f2` | Rail and privacy/roadmap support; not the dominant identity. |
| Muted petrol | `#27636b` | Secondary copy, accessible control borders on light surfaces. |
| White | `#ffffff` | Sample list, device content rows, unselected radio interior. |
| Apricot wash | `#ffe2cf` | Optional-drive surface. It never means payment due or an error. |

Color semantics matter more than a utility name. Decorative separators use `border`; essential controls use the stronger `control-border`. Do not use a subtle rule as the only control boundary. `selected`/`on-selected` are a pair; `hero`/`on-hero` are a pair; apricot remains an action accent in either theme.

### Drop-in brand tokens

Copy the following plain CSS block to `packages/brand/tokens.css` through the site developer's review. It has no Tailwind dependency. Font files and component styles are separate. Prototype shorthand maps directly: `--petrol` → `--fc-petrol`, `--glacier` → `--fc-glacier`, `--apricot` → `--fc-apricot`, `--warm` → `--fc-warm`, `--muted` → light `--fc-text-muted`.

The HTML study intentionally authors only the selected light composition, including its petrol hero. Dark is a semantic production specification below, not a claim that this draft has a theme switch. Do not invert the study with filters.

```css
/* FutureCheck — Open Relay. Plain shared tokens; no reset or font requests. */
:root {
  color-scheme: light;
  --fc-petrol: #073e43;
  --fc-glacier: #d7eef0;
  --fc-apricot: #ff9255;
  --fc-warm: #f7f6f2;
  --fc-bg: #d7eef0;
  --fc-surface: #ffffff;
  --fc-surface-muted: #f7f6f2;
  --fc-device: #d7eef0;
  --fc-drive: #ffe2cf;
  --fc-text: #073e43;
  --fc-text-muted: #27636b;
  --fc-border: #bdcdcd;
  --fc-control-border: #27636b;
  --fc-accent: #ff9255;
  --fc-accent-hover: #ffa574;
  --fc-accent-active: #f78140;
  --fc-on-accent: #073e43;
  --fc-selected: #073e43;
  --fc-on-selected: #ffffff;
  --fc-selection-soft: #d7eef0;
  --fc-radio-surface: #ffffff;
  --fc-focus: #073e43;
  --fc-hero: #073e43;
  --fc-on-hero: #d7eef0;
  --fc-hero-muted: #d7eef0;
  --fc-hero-rule: #527b7f;
  --fc-focus-on-hero: #d7eef0;
  --fc-font-display: "Chakra Petch", system-ui, sans-serif;
  --fc-font-body: "IBM Plex Sans", system-ui, sans-serif;
  --fc-text-body: 1.125rem;
  --fc-text-small: 0.875rem;
  --fc-text-caption: 0.75rem;
  --fc-text-h1: clamp(4.75rem, 7.1vw, 6.875rem);
  --fc-text-h2: clamp(2.75rem, 4.2vw, 3.875rem);
  --fc-text-h3: 1.5625rem;
  --fc-text-figure: 2.6875rem;
  --fc-leading-body: 1.6;
  --fc-leading-display: 1.05;
  --fc-tracking-display: -0.04em;
  --fc-radius-action: 0.5625rem;
  --fc-radius-control: 0.625rem;
  --fc-radius-row: 0.75rem;
  --fc-radius-panel: 1.5rem;
  --fc-radius-pill: 999px;
  --fc-space-unit: 0.25rem;
  --fc-rail: 13.5rem;
  --fc-page-max: 95rem;
  --fc-reading-max: 42rem;
  --fc-gutter: clamp(2rem, 4.2vw, 4.75rem);
  --fc-section: clamp(3rem, 6vw, 5.25rem);
  --fc-shadow-popover: 0 12px 32px rgb(7 62 67 / 0.15);
  --fc-duration-fast: 120ms;
  --fc-duration-response: 240ms;
  --fc-ease-out: cubic-bezier(0.2, 0.7, 0.2, 1);
}

/* Keep explicit and system-dark values identical. Explicit light wins. */
:root[data-theme="dark"] {
  color-scheme: dark;
  --fc-bg: #092d32;
  --fc-surface: #123b40;
  --fc-surface-muted: #0b252a;
  --fc-device: #17484e;
  --fc-drive: #44372f;
  --fc-text: #e4f4f4;
  --fc-text-muted: #b5d3d5;
  --fc-border: #3d6368;
  --fc-control-border: #83afb3;
  --fc-selected: #d7eef0;
  --fc-on-selected: #073e43;
  --fc-selection-soft: #17484e;
  --fc-radio-surface: #123b40;
  --fc-focus: #d7eef0;
  --fc-shadow-popover: 0 12px 32px rgb(0 0 0 / 0.3);
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]):not([data-theme="dark"]) {
    color-scheme: dark;
    --fc-bg: #092d32;
    --fc-surface: #123b40;
    --fc-surface-muted: #0b252a;
    --fc-device: #17484e;
    --fc-drive: #44372f;
    --fc-text: #e4f4f4;
    --fc-text-muted: #b5d3d5;
    --fc-border: #3d6368;
    --fc-control-border: #83afb3;
    --fc-selected: #d7eef0;
    --fc-on-selected: #073e43;
    --fc-selection-soft: #17484e;
    --fc-radio-surface: #123b40;
    --fc-focus: #d7eef0;
    --fc-shadow-popover: 0 12px 32px rgb(0 0 0 / 0.3);
  }
}
@media (min-width: 109.375rem) {
  :root { --fc-text-h1: 7.375rem; }
}
@media (max-width: 71.875rem) and (min-width: 50.0625rem) {
  :root {
    --fc-rail: 11.875rem;
    --fc-gutter: 2.125rem;
    --fc-text-h1: 4.8125rem;
    --fc-text-h2: 2.875rem;
  }
}
@media (max-width: 50rem) {
  :root {
    --fc-rail: 0rem;
    --fc-gutter: 1.5rem;
    --fc-text-body: 1rem;
    --fc-text-h1: clamp(3.0625rem, 10.8vw, 5rem);
    --fc-text-h2: 2.8125rem;
    --fc-text-figure: 2.4375rem;
  }
}
@media (prefers-reduced-motion: reduce) {
  :root { --fc-duration-fast: 0ms; --fc-duration-response: 0ms; }
}
```

`hero`, its reverse text/rule, and apricot action tokens intentionally remain stable in dark mode. Use `focus-on-hero` for every control on petrol, not the light-surface focus color. The low-contrast `hero-rule` is decorative only. No status/chart color scale is invented for a page without those controls; feature charts must receive their own tested, labeled series when built.

### Tailwind v4 bridge

In the site's CSS entry, import `tailwindcss`, then the shared tokens, then this bridge. Use the package's actual export path verified by dev. `@theme inline` lets generated utilities resolve the live semantic variables at the element, so light/dark and section overrides work. Do not place `@theme` inside a selector or media query, and do not export primitive hex values as semantic utilities that bypass theme changes.

```css
@theme inline {
  --color-fc-bg: var(--fc-bg);
  --color-fc-surface: var(--fc-surface);
  --color-fc-surface-muted: var(--fc-surface-muted);
  --color-fc-device: var(--fc-device);
  --color-fc-drive: var(--fc-drive);
  --color-fc-text: var(--fc-text);
  --color-fc-muted: var(--fc-text-muted);
  --color-fc-border: var(--fc-border);
  --color-fc-control: var(--fc-control-border);
  --color-fc-accent: var(--fc-accent);
  --color-fc-accent-hover: var(--fc-accent-hover);
  --color-fc-accent-active: var(--fc-accent-active);
  --color-fc-on-accent: var(--fc-on-accent);
  --color-fc-selected: var(--fc-selected);
  --color-fc-on-selected: var(--fc-on-selected);
  --color-fc-selection-soft: var(--fc-selection-soft);
  --color-fc-radio: var(--fc-radio-surface);
  --color-fc-focus: var(--fc-focus);
  --color-fc-hero: var(--fc-hero);
  --color-fc-on-hero: var(--fc-on-hero);
  --color-fc-hero-muted: var(--fc-hero-muted);
  --color-fc-hero-rule: var(--fc-hero-rule);
  --color-fc-focus-on-hero: var(--fc-focus-on-hero);
  --font-display: var(--fc-font-display);
  --font-body: var(--fc-font-body);
  --text-fc-body: var(--fc-text-body);
  --text-fc-small: var(--fc-text-small);
  --text-fc-caption: var(--fc-text-caption);
  --text-fc-h1: var(--fc-text-h1);
  --text-fc-h2: var(--fc-text-h2);
  --text-fc-h3: var(--fc-text-h3);
  --text-fc-figure: var(--fc-text-figure);
  --radius-fc-action: var(--fc-radius-action);
  --radius-fc-control: var(--fc-radius-control);
  --radius-fc-row: var(--fc-radius-row);
  --radius-fc-panel: var(--fc-radius-panel);
  --radius-fc-pill: var(--fc-radius-pill);
  --spacing-fc-rail: var(--fc-rail);
  --spacing-fc-gutter: var(--fc-gutter);
  --spacing-fc-section: var(--fc-section);
  --container-fc-page: var(--fc-page-max);
  --container-fc-reading: var(--fc-reading-max);
  --shadow-fc-popover: var(--fc-shadow-popover);
  --ease-fc-out: var(--fc-ease-out);
}
```

Examples: `bg-fc-bg text-fc-text font-body`; primary link `bg-fc-accent text-fc-on-accent hover:bg-fc-accent-hover rounded-fc-action`; rail `w-fc-rail`; container `max-w-fc-page px-fc-gutter`; focus `focus-visible:outline-3 focus-visible:outline-fc-focus focus-visible:outline-offset-4`. On the hero use `outline-fc-focus-on-hero`. Keep the browser's default root font size. Keep duration values as CSS variables (`duration-(--fc-duration-fast)`), not an assumed Tailwind duration namespace.

Production theme behavior: respect system preference unless an explicit Light/Dark choice exists. A labeled native System/Light/Dark select is sufficient; System removes `data-theme`. Apply the choice before first paint, without a crossfade. Persist only the site theme if policy review permits; the marketing site never stores financial or provider data. Publisher must reconcile site-only storage/hosting disclosures with the app policy before launch.

## Typography

### Families and licenses

- Chakra Petch 600: hero, H2, final display-sized sample total, and temporary text brand. Its angular silhouette is the identity; do not turn every small label into technical display type.
- IBM Plex Sans 400: paragraphs, controls, navigation, H3, sample rows, device contents, and legal copy. Real 500/600 faces may be added for production emphasis only if supplied and licensed; do not synthesize unavailable weights. The embedded study uses a 600 Chakra face and a Plex font payload.
- Self-host WOFF2 with `font-display: swap`, `font-synthesis: none`, and necessary punctuation/currency coverage. No runtime Google Fonts call. Existing reviewed binaries and full OFL notices are in `design/site/fonts/`, named `v2-chakra-petch-*` and `v2-ibm-plex-sans-*`; source details are in `fonts/v2-0411876b-SOURCES.md`. The standalone HTML embeds its own copies and notices. Font-license legal text is not pricing copy and must remain intact.
- Production family names use the full names in the tokens. Prototype `Chakra` and `Plex` aliases do not imply a different font. A system sans-serif fallback must preserve usable layout if fonts fail.

| Role | Family / weight | Size and behavior |
| --- | --- | --- |
| Hero H1 | Chakra Petch 600 | Desktop 76–110 px, 118 px only on very wide viewports; mobile 49–80 px. Line height 1.02–1.05, tracking −0.04 em. Two deliberate lines in the study; avoid breaks that clip at text zoom. |
| Section H2 | Chakra Petch 600 | 44–62 px desktop; approximately 45 px mobile, allowed to wrap. Line height 1.05. Legal H1 may use this quieter scale. |
| H3 | IBM Plex Sans 400 | 22–28 px, line height 1.25–1.4, tracking no tighter than −0.025 em. |
| Lead | IBM Plex Sans 400 | 20 px desktop, 17–18 px mobile; line height 1.55; 38–43 ch. |
| Body / legal | IBM Plex Sans 400 | 18 px desktop, 16 px mobile; 1.6 body, 1.75 legal. Legal width 60–68 ch, never stretched to fill the page. |
| Controls / navigation | IBM Plex Sans 400 | 15–17 px desktop, 14–16 px production mobile. The compact study nav/roadmap labels reach 13 px; production may enlarge rather than shrink to fit. |
| Caption | IBM Plex Sans 400 | Prefer 12–14 px production. A disclaimer is live HTML, not illegible image text. |
| Sample total | Chakra Petch 600 | 43 px desktop / 39 px mobile, 1.15 line height. Sample row figures remain Plex. |

Use tabular lining numerals for aligned amounts only. Sentence case, no dense uppercase labels, no opacity-based essential text. Values must not truncate; at tight widths the bill label can wrap above its amount.

## Layout

### Foundation and shared shell

- Desktop above 1150 px: fixed 216 px warm-white rail. Main and footer offset by the same rail token. Within the remaining width, color zones span the page and a centered 1520 px maximum content area uses fluid 32–76 px gutters. Do not put the entire site inside a floating card.
- Intermediate 801–1150 px: 190 px rail, 34 px gutters; tighter two-column spacing and a two-by-two schedule chooser. Do not squeeze legal prose beside an additional wide contents sidebar.
- At 800 px and below: rail becomes a normal header in document flow; main/footer offset is zero. Keep all three navigation links visible in a wrapping row, not an unexplained hamburger. One reading column and 24 px gutters. Support 320 px without page-level horizontal scroll; reduce gutters to 16 px if necessary for enlarged text.
- Normal section rhythm: approximately 78–84 px vertical padding desktop, 48–50 px mobile. Two-column editorial sections use approximately `.95fr 1fr` with 40–82 px gaps. Use `minmax(0, 1fr)` and `min-width: 0`; never force a section height.
- Navigation: home sections are “Your paychecks,” “Your data,” “Windows.” On legal pages, link back to the corresponding Home fragments and provide a real current-page Privacy/Terms link. Home logo links to `/`, not an external domain.
- A skip link precedes the rail/header. Keep focus rings outside clipping containers. The study's rail is intentionally persistent only on wider viewports; at zoom/reflow it must become the mobile header.
- Routes: `/`, `/privacy`, `/terms`. Internal links stay relative. One origin config value (`SITE_URL`, or dev's established equivalent) supplies canonical, OG, sitemap, and absolute asset URLs. `futurecheck.app` is planned, not registered; do not burn a domain into images or duplicate it across components.

### Home

1. Petrol hero. “For Windows” status; “Put bills in paycheck order.” H1; short benefit paragraph; apricot “See a paycheck plan” link to `#pay-period`; supporting no-account/no-bank-linking line. Bottom handoff line reads This paycheck → Next paycheck. No pricing, fake download, waitlist, or account flow.
2. Glacier paycheck section (`#pay-period`). Left: “Less calendar. More next paycheck.”, explanation, boundaries, dashboard/insights and payoff coverage. Right: white sample list with a two-button This/Next pressed-state switch. Default is every 2 weeks; the illustrated period, entries, and total change together. Label “Invented sample data” and “illustration, not a product screenshot” in live text.
3. Roadmap inset. Warm-white, visibly “Coming soon,” tagged `data-roadmap="pay-schedules"`. Weekly / Every 2 weeks / Twice a month (1st & 15th) / Monthly uses native radios and a fieldset/legend. It changes only the sample. Adjacent disclosure states the current Windows limitation. Hide/omit this entire element until approved without breaking the default This/Next demo. No preview setting is persisted.
4. Warm-white data section (`#privacy`). “Your plan. Not our data.” A clearly labeled illustration switch previews local and drive states; it does not perform sync. Local mode: a glacier home-base panel showing Bills, Payments recorded as paid, and Backup files saved by the person; balances/settings noted beneath. A compact adjacent offline explanation fills the second column with no reserved empty connector lane. Mobile stacks the explanation directly below the panel.
5. Drive mode extends the same home base rather than replacing it. A curved connector explicitly says “Encrypted before it leaves”; an apricot-wash panel shows encrypted sync files on the person's drive. Only financial sync data follows the path; local backup files remain local. Below, retain recovery-phrase loss, provider metadata visibility, and a separate visible Notion exception. A real “Read the privacy policy” link belongs in production; the study remains a single file.
6. Glacier Windows section (`#windows`). “Windows now. More places next.” Feature summary, followed by text-labeled planned macOS/iOS/Android/web. Supply an actual download only after Quantum provides an approved release URL. No disabled imitation of a download or active store badges for planned platforms.
7. Petrol footer. Name, Home/Privacy/Terms and source/issues links in production; optional labeled theme control. The standalone study identifies itself as a design study, not a launch. Keep sample disclosure on data-bearing exports. No invented legal entity, personal email, address, reviews, or attribution.

### Privacy

- Same rail/header and Open Relay colors; quiet glacier title band with H1 “Privacy policy,” source effective date, and short introduction. Do not reuse the huge promotional hero scale.
- Root `PRIVACY.md` is authoritative. Render its text, section order, lists, links, and effective date, not a marketing paraphrase. Required sections: What FutureCheck stores; Sync through your own cloud drive; Notion (optional); What we don't do; Removing your data; Changes and contact.
- Preserve exact permission identifiers `drive.appdata`, `openid`, `email`; Google Limited Use statement; provider metadata visibility; Notion's readable data; sign-in/token storage; uninstall, credential-removal, and saved-backup caveats. Do not replace these with a shield or “we see nothing.”
- White/warm-white reading surface, maximum 42 rem. On sufficiently wide screens a compact contents list can sit beside the prose; otherwise contents precedes it. Semantic anchors, readable 16–18 px text, 1.75 leading, underlined links. Long URLs/code wrap with `overflow-wrap: anywhere`.
- All policy text stays visible without JavaScript. No disclosure accordion for essential legal content, no animation of paragraphs, no summary-only replacement. Notion's exception remains visible.
- The app policy does not automatically describe hosting logs or site theme storage. Publisher/owner must resolve website-specific disclosure before launch; do not invent retention periods, legal bases, identities, or zero-log promises. No remote fonts, tracking scripts, widgets, ads, or analytics are requested.
- Print: white background, dark text, all content visible, effective date/source retained, navigation and theme controls removed; show meaningful link destinations. No clipped scrolling prose panel.

### Terms

- Same readable shell and width as Privacy. Use a quiet H1, visible draft status, and an owner-approved interim explanation until real terms exist. No invented effective date.
- Reserve copy slots for app/license, user-entered data and backups, optional third-party services, no bank connection or payment execution, reviewed disclaimers/liability, and changes/contact. This is structure only, not legal text.
- No invented jurisdiction, legal entity, pricing/license terms, consent checkbox, or “by browsing you agree” flow. Keep the route usable; exclude unfinished terms from indexing until approved. Terms and pricing require their own approval, not extrapolation from the art direction.

## Elevation & Depth

Flat by design. Color fields, scale, and spacing separate the page. The sample list and device rows have no drop shadow; there is no glass, perspective window stack, or blur behind text. `shadow-popover` is for an actual raised menu/select surface if needed, not every panel. Dark mode relies on surface differences and meaningful borders rather than heavier shadows.

## Shapes

Actions: 9 px radius. Radio option labels: 10 px. Small content rows and switch containers: 12 px. Major device/drive panels: 24 px desktop / 20 px mobile. The sample panel is 22 px in the study. Pills are reserved for compact status/platform tags, not every button.

Curved relay paths use rounded caps/joins and 2–3 px meaningful strokes. Icons are original, simple 20–24 px line art with approximately 1.7–2 px strokes, paired with words. Apricot circles are accents, not unlabelled success indicators. Device/drive panels are friendly containers, not empty ovals or technical network nodes.

## Components

### Actions, switches, and focus

- Primary action: link for navigation, at least 54–56 px tall in the reference, petrol text on apricot, no scale jump. Hover and active colors have dedicated tokens. Prose links stay underlined at rest.
- Paycheck and route switches are labeled `role="group"` containers of real buttons. Exactly one button per group exposes `aria-pressed="true"`. Both Enter and Space work; do not add tab roles without implementing a real tab pattern.
- Changed sample text/total and route description announce politely; do not move keyboard focus into the changed illustration. Use `hidden` for absent route pieces so they leave the accessibility tree. Device contents stay mounted in both modes.
- Focus: real 3 px outline, 4–5 px offset, `focus` on light surfaces / `focus-on-hero` on petrol. Never rely solely on a box shadow. In forced colors use `Highlight` and system control colors.
- Native controls retain semantics and keyboard handling; visual changes do not justify a custom div-based radio or fake payment button.

### Pay-schedule radio recipe

Use the native radio input, 18 × 18 px, inside its full clickable label (minimum 57 px high in the desktop study / 65 px mobile). Unchecked: white/semantic radio surface interior, 2 px muted-petrol/semantic control border, no inner dot. Checked: distinct 8 px filled dot in `selected`, with the same 18 px outer size; label gets selection-soft background and selected border. No layout shift between states. Theme the entire control, not only `accent-color`, so an OS/browser dark appearance cannot make every empty circle solid.

Production CSS equivalent, using the shared semantic tokens:

```css
.schedule-radio {
  appearance: none;
  -webkit-appearance: none;
  accent-color: var(--fc-selected);
  display: grid;
  place-content: center;
  flex: none;
  width: 18px;
  height: 18px;
  margin: 0;
  border: 2px solid var(--fc-control-border);
  border-radius: 50%;
  background: var(--fc-radio-surface);
}
.schedule-radio::before {
  content: "";
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: transparent;
}
.schedule-radio:checked { border-color: var(--fc-selected); }
.schedule-radio:checked::before { background: var(--fc-selected); }
.schedule-radio:focus-visible {
  outline: 3px solid var(--fc-focus);
  outline-offset: 4px;
}
@media (forced-colors: active) {
  .schedule-radio { appearance: auto; -webkit-appearance: auto; }
  .schedule-radio::before { display: none; }
  .schedule-radio:focus-visible { outline-color: Highlight; }
}
```

Tailwind maps: `appearance-none grid place-content-center size-[18px] shrink-0 m-0 border-2 border-fc-control rounded-full bg-fc-radio`; pseudo `before:content-[''] before:size-2 before:rounded-full before:bg-transparent checked:before:bg-fc-selected checked:border-fc-selected`; focus utilities from the bridge. Parent option uses `has-checked:bg-fc-selection-soft has-checked:border-fc-selected`. Retain the forced-colors CSS fallback; do not suppress user high-contrast settings. A native fieldset/legend and a shared `name` provide group behavior. Arrow keys change the checked option; Tab enters the group once; Space selects.

### Samples and illustration semantics

The revised study's invented fixture is the inline `bills`/`schedules` object in `v3-open-relay.html`: Sample home 480, Sample power 48, Sample streaming 12, Sample card 65, Sample loan 135, Sample installments 40. These are illustrative planned payments, not balances or suggested spending. The earlier Forecast Ledger fixture is not the v3 source.

Every exported sample must retain the invention/concept label. Re-derive totals from the displayed rows; never type an unverified total into art. Use relative periods rather than calendar dates. Static art does not imitate working payment controls. Keep essential text in live HTML; diagrams may be decorative when adjacent text fully explains them. Do not put a finance table into an enormous alt attribute.

### Motion

CSS only; no motion library required. No ambient motion, pulsing security icons, count-up currency, parallax, spinning devices, autoplay, or scroll-gated headings. Controls can transition color/underline in 120 ms. The sole sample response is a user-triggered 240 ms settle, at most 4 px translation. Route mode changes are immediate; preserve source order and the device's identity rather than animating files as if a real upload occurred.

Reduced motion resolves duration tokens to zero, removes component animations/transforms, and makes scrolling `auto`. Tokens alone are not enough: apply the selector to actual animations. Every default sample and all text are visible if JavaScript fails. No audio or flashes.

### Accessibility acceptance criteria

Target WCAG 2.2 AA; token checks are not certification.

- Normal text ≥4.5:1; genuinely large text ≥3:1; essential controls, focus, and diagram paths ≥3:1 against adjacent surfaces. Apricot connectors on light backgrounds need a petrol stroke or a petrol path; apricot alone is decorative there. No color-only availability, paid state, or selection.
- One H1, logical heading levels, semantic header/nav/main/footer, a skip link, `lang="en"` or `en-US`, accessible names, and correct hidden/pressed/checked semantics. No redundant focusable SVG icons.
- All important action hit areas target at least 44 × 44 px. The full radio label is the hit area, not its 18 px circle. Production mobile nav should enlarge the study's 40 px rows to meet this target.
- Test both route modes and all schedule × paycheck combinations at 320, 390, 800, 1024, and 1440 px; native keyboard arrows, Tab/Shift+Tab, Enter/Space; visible focus; no traps. Text and money values wrap without clipping.
- Test 200% text zoom, 400% page reflow, and WCAG text-spacing overrides (1.5 line height, 2 em paragraph spacing, 0.12 em letter spacing, 0.16 em word spacing). No focus clipping or page-level horizontal scroll.
- Test system and explicit themes separately; native appearance under dark OS settings; forced colors; reduced motion; font/JS/resource failure. Do not globally set `forced-color-adjust: none` or disable zoom.
- Screen-reader testing with VoiceOver/NVDA is still required for the implemented site: check radio group labels/checked announcement, pressed switches, polite updates, hidden drive content, reading order, and legal contents navigation. Automated checks alone do not establish this.
- Verify policy fidelity against root `PRIVACY.md`; check real release links, pricing exclusions, roadmap visibility, and legal approval before launch. Browser proof of this standalone study does not replace a production build or site audit.

## Do's and Don'ts

- Do lead with paycheck organization and one practical next step. Don't imply banking access, automatic money movement, AI, investment growth, or guaranteed debt outcomes.
- Do keep the petrol/glacier identity and one apricot action. Don't restore Forecast Ledger's serif/violet palette, fill the page with orange, or make it a dev console.
- Do show substance inside the device in both states. Don't reserve a blank sync lane in local mode or imply backups are automatically uploaded/encrypted.
- Do distinguish optional drive encryption, readable Notion data, and provider metadata. Don't promise that everything is always encrypted or that providers see nothing.
- Do retain visible sample/roadmap disclosures. Don't capture a live financial database or borrow realistic institution identities, dates, personal details, or account screens.
- Do label Windows honestly and other platforms as planned. Don't publish undecided pricing or treat a cost-of-operation goal in an older plan as a consumer price decision.

## Studio Asset Brief

Owner: studio (Ermac). Direction: Open Relay. This brief defines production targets, not delivered assets. The logo redesign is already studio-owned under `design/site/logo/`; do not initiate a competing logo job or overwrite that directory. Claude coordinates final artwork review and Quantum approves.

| Asset | Sizes / formats | Direction and acceptance |
| --- | --- | --- |
| Logo, wordmark, micro mark, favicon | Use `design/site/logo/` approved SVG masters/manifest; browser icons need 16/32/48 px proof plus 180/192/512 px outputs where requested. | Studio's replacement is authoritative. Require petrol, reverse, monochrome, and crop-safe variants; keep exact FutureCheck capitalization. No domain in pixels, no new construction based on the old ledger logo. |
| Relay motif | Editable SVG, 1200 × 240 desktop and 360 × 160 mobile, light/dark and monochrome. | One rounded path bridging equal-status periods, not an up-and-right profit chart. Petrol stroke on light fields; apricot accent has a petrol edge where meaningful. |
| Paycheck illustration | Editable SVG master 1200 × 1000, mobile 720 × 1040; SVG first, optional WebP/AVIF ≤160 KB. | Open Relay's white sample-list grammar, not a product screenshot. Use only the v3 inline fixture, verified totals, relative periods, and persistent invented-data caption. Prefer live HTML for text and interaction. No need to rasterize a working HTML demo. |
| Device / optional-drive composition | Editable SVG 1200 × 760 desktop and 720 × 1120 mobile; light/dark/monochrome. | Device contains bills, recorded payments, saved backups, plus balances/settings note. Drive state extends that same device through a labeled encrypted channel; backup files remain local. No giant shield, server rack, recovery words, or provider account identity. Notion exception remains separate live copy. |
| Payoff explainer if needed | Editable SVG 960 × 720, light/dark. | Abstract ordered blocks: Snowball (smallest balance first), Avalanche (highest APR first). No numerical rates/balances or promised payoff date. No new hero metaphor. |
| Open Graph | Editable SVG master plus 1200 × 630 PNG, ≤300 KB target, 64 px safe inset. | Petrol field, approved studio lockup, short approved paycheck-organizing headline in Chakra, a simple apricot relay. Data-free preferred. No planned domain, unapproved price, real finance details, or fake store badges. |

Production rules: original vector-led art; editable text/source plus outlined final lockup where needed; self-host fonts and retain OFL attribution; no external SVG scripts or remote resources; no AI-rendered letters or figures; generic device frames only. Deliver alt/decorative intent and safe-crop guidance in a manifest. File prefix `0411876b-fc-`, with `-light`/`-dark` where relevant. Any artwork showing data carries its sample label through every crop.

### Delivery and review boundary

All authored revisions stay in `design/site/`, excluding studio's `logo/`. No edits to `apps/site` or `packages/brand` are performed by this design task. The mesh share was not mounted at the prerequisite check; the explicitly requested local project directory is the handoff location. No copies are placed under an unmounted share.

The v3 HTML is the appearance/interaction reference; this file supplies the production token and layout contract. Existing v3 browser/package scripts can refresh the standalone proof. Any old evidence hash or historical artifact is not a proof of the revised HTML. See `v3-0411876b-revision-review.md` for this revision's checks and remaining gates. Claude reviews in the originating T3 thread; Quantum approves. A Kanban producer task, if used, must go to critic review rather than self-completion.
