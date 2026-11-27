# FutureCheck: three independent brand directions

Design round v2. Coordination: T3 thread `0411876b-02ba-457a-ab44-db4a4b326d7b`.
Status: resumed v2 verification complete; ready for Claude's review and Quantum's selection, not approved production branding.
Scope: only `design/site/`. The existing `DESIGN.md` remains unchanged.

## Before building: subject and decisions

FutureCheck's distinctive subject is the space between two biweekly paychecks, not a bank account or an AI forecast. These studies explain where bills belong, what gets marked paid, and where the data goes. They use real draft copy and invented sample bills, with no real dates, people, institutions, or financial data. The site is not required to resemble WinUI. A selected direction can instead inform the app's later redesign.

The frontend-design skill was read at the exact supplied path. Each concept below changes the organizing metaphor, typography, color relationships, composition, and interaction—not just the palette. No stock photos, hero screenshots, review quotes, store badges, shadowed floating windows, decorative sequence numbers, editorial serifs, violet accents, or placeholder copy.

### 1. Payday Relay

An energetic identity built from the handoff between one paycheck and the next. Condensed, athletic typography meets broad cobalt and yellow lanes; the hero's paired flags introduce the product's actual time structure instead of a generic dashboard. This is distinct in finance because its visual metaphor is cadence and organization, not wealth, banking, or speculative growth. The risk is sounding like competitive performance or implying that the app moves money; plain bill-tracking copy, a restrained motion budget, and explicit “mark paid” language must counter that reading.

- File: `v2-payday-relay.html`.
- Palette: Relay blue `#1937C4`, Signal yellow `#FFDB45`, Ice blue `#C9E5FF`, White `#FFFFFF`, Deep blue `#122357`.
- Type: Barlow Condensed Bold for large display; Manrope for body, navigation, and figures.
- Composition: left-aligned, oversized three-line headline; a right-side handoff graphic; full-width alternating color fields rather than a card grid.
- Motion: a single lane-entry reveal on load; reduced motion disables it. Pay-period selection changes only the sample bill list and its total.
- Responsive rule: paired flags become two compact horizontal lanes below the headline; the sample list stays a real list, never a miniature desktop window.

```text
Wordmark                              navigation
Headline, enormous       this paycheck =====
Copy and action               ===== next paycheck
================================================
Period explanation       selectable sample bill list
================================================
Data stays local         optional encrypted path
Windows availability and roadmap
```

### 2. Pocket Logic

A useful, tactile identity about giving every bill a place. Broad coral fields, plum lettering, and mint pocket forms replace finance's grids and dashboards; Bricolage Grotesque supplies a deliberately human, variable-width voice. The hero is an original geometric pocket illustration made from labeled paycheck tabs, not a borrowed app mockup. Its risk is looking too toy-like or evoking cash-envelope budgeting, which FutureCheck does not offer; the copy must stay about organizing and marking bills, with no invented allocation or money-moving features.

- File: `v2-pocket-logic.html`.
- Palette: Coral `#F57568`, Plum `#481C3B`, Mint `#C5F0DB`, Peach `#FFE1CE`, White `#FFFFFF`.
- Type: Bricolage Grotesque throughout, using different weights and optical sizing rather than a decorative second family.
- Composition: centered headline and oversized pocket illustration; staggered two-column narrative sections; large, rounded silhouettes only where they encode a pocket or a completion state.
- Motion: no ambient animation. A user can mark one fictional bill paid and undo it; the label and completion count change together.
- Responsive rule: illustration simplifies to two readable tabs inside a single pocket; asymmetric sections become a single reading column.

```text
Wordmark                         navigation
              Headline
                Copy
        [ this ]    [ next ]
       (        one pocket        )
Explanation               sample bill checklist
         Data ownership, in three disclosures
Windows availability and roadmap
```

### 3. Open Circuit

A precise, architectural identity for a finance app without a central service. Angular Chakra Petch headings and IBM Plex Sans body text occupy a glacier-and-petrol control surface; a visible data-path switch illustrates local storage versus optional encrypted drive sync. A fixed desktop navigation rail makes the page feel like an owned instrument rather than a funnel. It is distinct because privacy becomes a legible system topology, not a shield icon or an unverifiable security slogan. The risk is over-indexing on engineering and looking like a developer tool; sentence-case copy, readable labels, and a concrete pay-period example keep the person ahead of the protocol.

- File: `v2-open-circuit.html`.
- Palette: Glacier `#D7EEF0`, Petrol `#073E43`, Apricot `#FF9255`, Muted petrol `#27636B`, White `#FFFFFF`.
- Type: Chakra Petch Semibold for headings; IBM Plex Sans for all body content and controls. No terminal styling or tiny monospace labels.
- Composition: persistent desktop side rail; broad horizontal content zones; squared data-path geometry with clipped corners reserved for the central diagram.
- Motion: user-triggered data-path selection only. No pulsing dots, animated gradients, or neon glow.
- Responsive rule: navigation rail becomes a normal top bar; device/drive diagram becomes a vertical route; wide tables are replaced by stacked semantic groups.

```text
Brand rail | Headline across main area
Navigation | Plain product explanation
           | this paycheck | next paycheck
           |--------------------------------
           | Local only / Encrypted drive
           | device ===== encrypted ===== drive
           | Notion exception, separately
           | Windows availability and roadmap
```

## Pre-build critique and revisions

A first impulse toward an orange-on-black “financial cockpit” was rejected: it would repeat the generic near-black-plus-accent formula warned about in the skill. Open Circuit instead uses a pale glacier canvas and a substantial petrol ink system. A handoff graphic made of floating cards was rejected for Relay; continuous flat lanes and broad fields carry the metaphor without a SaaS mockup. Pocket Logic originally suggested envelopes; the illustration is now explicitly two pay-period tabs in a common pocket so it does not promise an envelope-budgeting feature. No study uses the old serif, paper, hairline, or violet system.

## Review boundaries

All visible money is intentionally invented sample data; there are no calendar dates or due-day numbers. The interactions are illustrations, not payment execution, real sync, or a working app. Windows is the only current platform. macOS, iOS, Android, and web are labeled as planned. There is no FutureCheck account, bank linking, or transaction import. Drive sync uses the user's own Google Drive or OneDrive where offered and is end-to-end encrypted with a recovery phrase. Provider-visible bookkeeping and file metadata are disclosed. The optional Notion connector is explicitly not encrypted by FutureCheck. No fabricated testimonials, launch promises, downloads, pricing tiers, or third-party badges.

All navigation uses local fragment links. No unregistered marketing domain is embedded. The eventual implementation should set its origin once through `SITE_URL`, as specified by the site plan, after the domain is chosen.

## Selection

Recommendation after desktop and mobile visual proofing: **Payday Relay**. Its paired, offset paycheck lanes make FutureCheck's biweekly organizing idea visible before anyone reads a feature list. Barlow Condensed gives the headline a recognizable silhouette without sacrificing a legible, separate body-text voice. The cobalt/yellow/ice fields maintain a strong entrance at 1440 px and become readable stacked lanes at 390 px; the system can carry into pay-period headers, selected states, and completion cues in the app. It is product-specific rather than a generic promise of financial wellbeing.

Pocket Logic is the strongest warmer alternative, but the pocket remains easier to mistake for envelope budgeting, and its broad curves are less useful for a dense bill-tracking interface. Open Circuit explains local storage and encrypted sync best, but leading with systems architecture makes the product feel like a technical utility before it feels like an everyday bill tracker. Neither alternative is discarded as low-quality; Relay wins on immediate product comprehension and a reusable visual grammar. “Payday Relay” is an internal direction name only: the product and public wordmark remain **FutureCheck**, not a payday-lending brand.

Keep Relay's motion to the single handoff reveal and keep the explicit no-bank-linking/no-payments copy. Do not turn this into a sports campaign, introduce growth charts, or combine all three directions into a compromise palette. Quantum's selection is still required before the production design specification changes.

## Verified single-file delivery

The interrupted v2 studies were resumed, not rebuilt. The five existing WOFF2 binaries are now encoded as `data:font/woff2;base64,...` in their respective HTML files. Font copyright notices and full SIL Open Font License text also travel inside HTML comments. CSS, SVG artwork, and interaction JavaScript are inline. No study needs the adjacent `fonts/` directory, a web server, an installed typeface, or a network request to display its intended typography.

`v2-0411876b-embed-fonts.py` is the repeatable packaging step. `v2-0411876b-embedded-fonts.json` records the original font byte hashes and packaged HTML byte hashes. Verification decodes each embedded font and checks it byte-for-byte against its original WOFF2 asset. Existing font sources and licenses remain available under `fonts/` for production implementation; they are no longer runtime dependencies of the studies.

## Browser and static check results

Verified in Chrome 156 using the final HTML file hashes. HTTP(S), adjacent font-file loading, and browser cache were disabled during browser proofing. Each HTML was tested directly with `file://` and separately inserted into an `about:blank` iframe using `srcdoc`, which has no usable relative font directory. No layout or interaction failure was found in the final studies; the substantive delivery fix was making the fonts genuinely portable.

| Study | Direct 390 / 1440 px | srcdoc 390 / 1440 px | Keyboard | Reduced motion |
| --- | --- | --- | --- | --- |
| Payday Relay | Pass / Pass | Pass / Pass | Pass: period selection, navigation, focus | Pass: reveal removed; instant scrolling |
| Pocket Logic | Pass / Pass | Pass / Pass | Pass: paid/undo, disclosures, navigation, focus | Pass: no ambient animation; instant scrolling |
| Open Circuit | Pass / Pass | Pass / Pass | Pass: local/encrypted route, navigation, focus | Pass: no ambient animation; instant scrolling |

- Six direct-file viewport checks and six srcdoc viewport checks: no horizontal overflow, no right-edge text clipping, all intended font faces loaded, and four main sections in every study.
- 52 keyboard assertions passed: Tab reaches every visible control in DOM order with a visible 3 px outline; Shift+Tab reverses order; the skip link bypasses navigation; navigation reaches the privacy section; Enter and Space operate the sample controls. Native Pocket Logic disclosures close and reopen with Space.
- All six ordinary-motion viewport checks also passed: only Relay declares a single load reveal; reduced motion removes that reveal and switches smooth scrolling to instant scrolling in all studies.
- 27 unique rendered text-color/background/size combinations passed their WCAG AA text-contrast threshold. The lowest measured ratio was 5.068:1. This is a scoped text-contrast check, not a claim of a complete accessibility audit.
- Zero remote resource loads. All internal fragment targets and ARIA references resolve; IDs are unique; every study has one h1. Invented sample totals were re-derived from the displayed entries.
- Full-page desktop and mobile captures were visually reviewed for composition, reading order, illustration labels, and section boundaries. There are separate hero and srcdoc-hero captures for comparison.
- The existing `DESIGN.md` hash remains unchanged. Only files under `design/site/` were authored or updated. No commit, push, or deployment was performed.

Evidence: `v2-0411876b-browser-checks.json`, `v2-0411876b-verification.json`, and `v2-0411876b-proofs/`. Re-run the static and recorded-browser checks with `python3 design/site/v2-0411876b-verify.py`. To refresh browser evidence, load `v2-0411876b-browser-proof.py` in the browser-use helper context and call `run(js, cdp, goto_url, wait_for_load, capture_screenshot, reset=True)`.

These artifacts are for review and direction selection. They are not an approved brand, a live site, or a downloadable app build.
