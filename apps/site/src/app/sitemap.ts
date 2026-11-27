import type { MetadataRoute } from "next";
import { absoluteUrl } from "@/site.config";

// Built once into out/sitemap.xml by the static export.
export const dynamic = "force-static";

// /terms is left out while it is a placeholder (it is also marked noindex).
const paths = ["/", "/privacy"] as const;

export default function sitemap(): MetadataRoute.Sitemap {
  return paths.map((pathname) => ({ url: absoluteUrl(pathname) }));
}
