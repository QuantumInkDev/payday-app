import sitemap from "@astrojs/sitemap";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "astro/config";
import { siteConfig, unlistedPaths } from "./src/site.config";

// Static output: every page is prerendered to dist/ at build time. Vercel serves it as-is, so no
// adapter is needed.
export default defineConfig({
  site: siteConfig.baseUrl,
  output: "static",
  // Canonical URLs have no trailing slash (/privacy, not /privacy/). Pages still build to
  // privacy/index.html, which Vercel serves at /privacy.
  trailingSlash: "never",
  build: { format: "directory" },
  integrations: [
    sitemap({
      filter: (page) => !unlistedPaths.includes(new URL(page).pathname),
    }),
  ],
  vite: {
    plugins: [tailwindcss()],
  },
});
