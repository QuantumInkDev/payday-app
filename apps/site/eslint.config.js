import js from "@eslint/js";
import astro from "eslint-plugin-astro";
import { defineConfig, globalIgnores } from "eslint/config";
import tseslint from "typescript-eslint";

export default defineConfig([
  globalIgnores(["dist/**", ".astro/**"]),
  js.configs.recommended,
  tseslint.configs.strict,
  // Last, so its parser handles .astro files (with the TypeScript parser for frontmatter).
  astro.configs.recommended,
  {
    rules: {
      "@typescript-eslint/no-explicit-any": "error",
    },
  },
]);
