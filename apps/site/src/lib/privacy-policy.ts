import { readFile } from "node:fs/promises";
import path from "node:path";

/**
 * The privacy policy's single source of truth is PRIVACY.md at the repository root. Pages read it
 * at build time; the build runs with apps/site as the working directory (pnpm, Turborepo and Vercel
 * all do this), so the file is two levels up.
 */
const PRIVACY_POLICY_PATH = path.join(process.cwd(), "..", "..", "PRIVACY.md");

export async function loadPrivacyPolicy(): Promise<string> {
  return readFile(PRIVACY_POLICY_PATH, "utf8");
}
