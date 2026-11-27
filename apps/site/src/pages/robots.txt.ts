import type { APIRoute } from "astro";
import { absoluteUrl, siteConfig } from "../site.config";

// Prerendered once into dist/robots.txt (the site has static output).
export const GET: APIRoute = () => {
  const body = siteConfig.allowIndexing
    ? ["User-agent: *", "Allow: /", "", `Sitemap: ${absoluteUrl("/sitemap-index.xml")}`, ""]
    : // Vercel preview and development deployments.
      ["User-agent: *", "Disallow: /", ""];
  return new Response(body.join("\n"), {
    headers: { "Content-Type": "text/plain; charset=utf-8" },
  });
};
