# @futurecheck/brand

Brand tokens shared by `apps/site` (and later `apps/web`).

- `tokens.css`: CSS custom properties (`--fc-*`) for the Open Relay direction, copied as given from
  `design/site/DESIGN.md` ("Drop-in brand tokens"). Light values on `:root`; dark values apply under
  `prefers-color-scheme: dark` or an explicit `data-theme="dark"` on the root element, and
  `data-theme="light"` forces light. Change DESIGN.md first, then this file.
- Import it from CSS: `@import "@futurecheck/brand/tokens.css";`
- `apps/site/src/styles/global.css` maps the tokens to Tailwind utilities in an `@theme inline` block
  (the DESIGN.md "Tailwind v4 bridge": for example `--fc-accent` becomes `bg-fc-accent`). Add a
  mapping there when you add a token here.
- No fonts and no font requests live here. The site self-hosts Chakra Petch and IBM Plex Sans from
  `apps/site/public/fonts/`, with their OFL texts beside them.

There is no build step: the package ships the CSS file as-is.
