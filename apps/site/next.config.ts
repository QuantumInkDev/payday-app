import path from "node:path";
import { fileURLToPath } from "node:url";
import type { NextConfig } from "next";

const appDir = path.dirname(fileURLToPath(import.meta.url));

const nextConfig: NextConfig = {
  // Every page is static, so the site builds to a plain static export in out/.
  output: "export",
  // The image optimizer needs a server; a static export serves images as-is.
  images: { unoptimized: true },
  // The pnpm workspace root, so Turbopack resolves workspace packages from there.
  turbopack: { root: path.join(appDir, "..", "..") },
};

export default nextConfig;
