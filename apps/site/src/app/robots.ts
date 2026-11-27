import type { MetadataRoute } from "next";
import { absoluteUrl, siteConfig } from "@/site.config";

// Built once into out/robots.txt by the static export.
export const dynamic = "force-static";

export default function robots(): MetadataRoute.Robots {
  if (!siteConfig.allowIndexing) {
    // Vercel preview and development deployments.
    return { rules: { userAgent: "*", disallow: "/" } };
  }
  return {
    rules: { userAgent: "*", allow: "/" },
    sitemap: absoluteUrl("/sitemap.xml"),
  };
}
