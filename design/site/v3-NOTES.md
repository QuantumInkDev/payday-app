# Open Relay — v3 direction

Review draft for T3 thread `0411876b-02ba-457a-ab44-db4a4b326d7b`.
Deliverable: `v3-open-relay.html`. Product name: FutureCheck. Direction names are internal only.

Revision: Quantum selected Open Relay. The current production contract is `DESIGN.md`, which replaces Forecast Ledger. The revised HTML removes pricing claims, gives native radios explicit brand paint and a high-contrast fallback, and replaces empty storage ovals with a persistent device-content composition that optional drive sync extends. Logo redesign remains studio-owned in `logo/`; the study mark is temporary. See `v3-0411876b-revision-review.md` for the latest evidence and review boundary.

## Brief and pre-build decisions

Quantum selected Open Circuit's visual style and requested Payday Relay's copy, with a softer technical edge. This supersedes the designer's earlier preference for Relay; it is not final production approval. The exact supplied frontend-design skill was read before building.

The subject is the handoff from this paycheck to the next. It is not banking, moving money, a trading terminal, or an architecture product. The entrance stays bold and the reading stays plain.

### Compact system

- Petrol `#073E43`: primary ink and broad hero field.
- Glacier `#D7EEF0`: primary light canvas and reversed display type.
- Apricot `#FF9255`: the main action and meaningful connections.
- Warm white `#F7F6F2`: rail and sample surface; a supporting neutral, not a beige identity.
- Muted petrol `#27636B`: secondary text on light fields.
- White `#FFFFFF`: sample-list reading surface.
- Chakra Petch 600: wordmark, largest display headlines, and the single large sample total. That display-sized total is the deliberate exception that ties the practical list back to the brand. Keep the recognizable angular silhouette without making every label sound mechanical.
- IBM Plex Sans 400: body, navigation, buttons, sample row figures, and secondary headings. Original Open Circuit pairing retained; hierarchy comes from scale and placement, not synthetic bold.
- Desktop: persistent 216 px rail, left-aligned full-width color zones, approximately 60 px content gutters at 1440 px, generous section spacing.
- Mobile: rail becomes a normal header with a wrapping, fully available navigation row. One reading column; controls and figures stay full-size rather than shrinking a desktop screenshot.

```text
Warm rail  | Petrol hero: Put bills in / paycheck order.
           | Plain description, orange action
           | This paycheck —— soft handoff —— Next paycheck
           |------------------------------------------------
           | Less calendar.          Switchable sample list
           | More next paycheck.     This / Next
           | Coming soon: schedule choice, changes the sample
           |------------------------------------------------
           | Your plan. Not our data.
           | Local / encrypted-drive illustration, rounded path
           | Privacy limits and Notion exception
           |------------------------------------------------
           | Windows now. More places next.
```

### Critique before construction

A full cream conversion was rejected: it would discard the glacier/petrol identity Quantum liked. Replacing Chakra with Relay's Barlow was rejected for the same reason. A floating app window and a repeating card grid were rejected because this is a plain explanation of paycheck organization, not a dashboard screenshot. A hand-drawn mascot or envelope illustration was rejected because it would introduce another metaphor and could imply envelope budgeting. The useful merger is Open Circuit's structure and color, Relay's human language, and a softer connection geometry—not a palette compromise.

## What softens the technical edge

- Relay's plain headlines lead: “Put bills in paycheck order.”, “Less calendar. More next paycheck.”, “Your plan. Not our data.”, and “Windows now. More places next.” Main copy is cadence-neutral.
- Navigation says “Your paychecks” and “Your data,” not “Data path.” Round navigation markers replace square terminals.
- Chakra is limited to the wordmark and large display type, including the sample total. The sample rows, smaller headings, and every control use Plex.
- A warm-white rail, white list, and warm-neutral roadmap inset sit beside glacier and petrol. Orange remains an action color, not a field of warning signals.
- Rounded connections and a friendly home-base composition replace empty storage ovals and bordered architecture nodes. Bills, recorded payments, and saved backups remain visible in both modes; optional encrypted drive sync extends the composition without implying that backup files upload.
- Larger gaps and a less dense sample list keep the page from reading like a console.
- Sentence-case microcopy and native controls favor everyday language. A brief user-triggered list change adds warmth without ambient animation.

## Product and implementation boundaries

- This is an interactive design illustration, not a product screenshot, an app, or a download. All financial entries and amounts are invented and labeled sample data. There are no actual calendar dates, real due days, personal details, institution names, credentials, or client IDs.
- Main copy talks about “your paychecks,” never defines the product as biweekly. The roadmap disclosure explicitly states that Windows currently supports every 2 weeks only.
- The schedule chooser is inside `data-roadmap="pay-schedules"`, labeled “Coming soon.” Weekly, Every 2 weeks, Twice a month (1st & 15th), and Monthly change both the displayed sample period and its bill list. The 1st/15th are the requested generic schedule definition, not anyone's actual pay dates. Relative labels distinguish weekly periods, 2-week periods, halves of a sample month, and whole sample months.
- Default is Every 2 weeks. The “This paycheck / Next paycheck” controls work independently of the schedule. Dev can omit the tagged roadmap element and retain the default, cadence-neutral sample interaction; do not persist a preview selection as a real app setting.
- Local storage and optional encrypted sync use the user's own Google Drive, or OneDrive where offered. The app requires no FutureCheck account, not “no cloud-provider sign-in.” Provider-visible non-secret bookkeeping, encrypted file names/times, and unrecoverability after loss of all devices and the phrase remain disclosed.
- Notion is separate and optional; selected data sent to Notion is not encrypted by FutureCheck. No bank linking, transaction import, or payment execution is implied.
- Windows is current. macOS, iOS, Android, and web are planned, not available. No store badges, fabricated testimonials, release dates, or download claims.
- All links are local fragments. No planned domain is hard-coded; production should continue to use the site's single origin config value. This study does not reproduce or replace the full Privacy page; `PRIVACY.md` remains authoritative.
- CSS, SVG, JavaScript, WOFF2 binaries, and their full OFL notices travel inside the final HTML. No adjacent fonts or remote resources are needed.
- Native buttons support Enter and Space; native radio inputs support arrow keys and Space. Selected buttons expose `aria-pressed`. The sample panel and route description announce updates politely. Visible focus, a skip link, semantic landmarks, and reduced-motion behavior are required.

## Executed verification

The finished HTML was exercised in Chrome 156 with network resources, the adjacent fonts directory, and browser cache blocked. Evidence is tied to the final HTML SHA-256 recorded in the JSON reports.

- 87 keyboard, interaction, native-radio-paint, and motion assertions passed across 39 recorded views in the revision.
- Required 390 px and 1440 px direct-file views passed, including all four schedules and both paycheck selections. Labels match the schedule; each total was independently re-derived from the displayed sample rows. Native radio arrows and Space work; paycheck and route buttons work with Enter and Space.
- Additional 320 px, 800 px, 801 px, 1024 px, and 1150 px views passed in both route modes. No horizontal overflow or right-edge text clipping was detected in any recorded view. Both intended font faces loaded.
- Direct-file and isolated `srcdoc` checks passed at 390 px and 1440 px. The iframe has no adjacent font directory to fall back to; its sample buttons also execute.
- Hiding the tagged roadmap retains the default sample. Omitting the entire tagged element also works: the feedback update tolerates the element's absence. No preview setting is persisted.
- Tab reaches all visible controls in DOM order with one native radio-group stop and a visible 3 px outline. Shift+Tab reverses order. The skip link bypasses navigation, and the data navigation link reaches its section.
- Ordinary motion has no ambient animation; only a 240 ms user-triggered list response. Reduced motion disables that response and smooth scrolling.
- All 18 unique recorded text-color/background/size combinations passed their WCAG AA text thresholds. Lowest measured contrast: 5.337:1. This is a scoped rendered-text contrast test, not a complete accessibility audit. VoiceOver/NVDA announcements were not exercised.
- Zero remote resources loaded. Both embedded WOFF2 payloads byte-match Open Circuit's original font files, and full OFL notices are in the HTML. IDs are unique; fragment and ARIA references resolve; there is one h1.
- Desktop and mobile hero/full-page captures were visually inspected. The mobile rail becomes a readable header; no navigation item is discarded. The route, list, and roadmap remain full-size reading surfaces.
- The earlier draft received an internal read-only critic pass. That earlier pass does not approve this revision. Claude's review and Quantum's approval are still required; this is not a production release or branding sign-off.

### Evidence and repeatability

- `v3-0411876b-proofs/`: desktop/mobile hero, full page, local-device, encrypted-drive, and focused-radio views.
- `v3-0411876b-browser-checks.json`: actual browser views, interaction assertions, and rendered color samples.
- `v3-0411876b-static-checks.json`: font byte hashes, HTML hash, resource and reference checks.
- `v3-0411876b-verification.json`: aggregated result, contrast calculations, and final hash.
- Run `python3 design/site/v3-0411876b-package.py` to verify packaging and references; it only inserts fonts if the packaging markers exist.
- Run `python3 design/site/v3-0411876b-verify.py` to re-derive the aggregate and contrast from the recorded browser evidence; it rejects a stale HTML hash.
- To refresh browser evidence, load `v3-0411876b-browser-proof.py` in the browser-use helper context and call `run(js, cdp, goto_url, wait_for_load, capture_screenshot)`. Run the two Python checks afterward.
- Run `node design/site/v3-0411876b-design-proof.mjs` to verify the revised DESIGN.md light/dark contrast pairs, dark-selector parity, token aliases, and actual Tailwind v4 utility compilation. This reads the site's installed dependency without changing application styles.

The production site was not changed, so no application build or native .NET test result is claimed for this design-only task.

## Delivery scope

Only `design/site/` is authored. No application changes, commit, push, merge, deployment, domain operation, or production-design replacement. The mesh share is not mounted, so the requested local project path is the delivery location. Claude reviews in the supplied thread; Quantum approves. No additional generation or another bot's work is needed for this study.
