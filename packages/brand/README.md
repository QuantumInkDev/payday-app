# @futurecheck/brand

Brand tokens shared by `apps/site` (and later `apps/web`).

- `tokens.css`: CSS custom properties (`--fc-*`). The values are placeholders until the designer
  fills them from `design/site/DESIGN.md`.
- Import it from CSS: `@import "@futurecheck/brand/tokens.css";`
- `apps/site/src/app/globals.css` maps the tokens to Tailwind utilities in an `@theme inline` block
  (for example `--fc-color-accent` becomes `bg-accent` and `text-accent`). Add a mapping there when
  you add a token here.

There is no build step: the package ships the CSS file as-is.
