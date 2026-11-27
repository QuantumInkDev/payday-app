/**
 * Site-wide configuration: name, base URL, navigation and footer links.
 *
 * The base URL is the only place the domain appears. Set NEXT_PUBLIC_SITE_URL to the canonical
 * origin (for example https://futurecheck.app once the domain is live). Without it, a Vercel build
 * uses the project's production URL, then the deployment URL, and a local build uses localhost.
 */

export type NavLink = {
  label: string;
  href: string;
  external?: boolean;
};

export type FooterGroup = {
  title: string;
  links: readonly NavLink[];
};

const LOCAL_URL = "http://localhost:3000";
const REPO_URL = "https://github.com/QuantumInkDev/payday-app";
const ISSUES_URL = `${REPO_URL}/issues`;

function normalizeOrigin(value: string): string {
  const withProtocol = /^https?:\/\//i.test(value) ? value : `https://${value}`;
  return new URL(withProtocol).origin;
}

function resolveBaseUrl(): string {
  const candidates = [
    process.env.NEXT_PUBLIC_SITE_URL,
    process.env.VERCEL_PROJECT_PRODUCTION_URL,
    process.env.VERCEL_URL,
  ];
  for (const candidate of candidates) {
    const trimmed = candidate?.trim();
    if (trimmed) {
      return normalizeOrigin(trimmed);
    }
  }
  return LOCAL_URL;
}

/** True only on Vercel preview and development deployments, which should not be indexed. */
const isNonProductionDeployment =
  process.env.VERCEL_ENV !== undefined && process.env.VERCEL_ENV !== "production";

export const siteConfig = {
  name: "FutureCheck",
  tagline: "Plan every paycheck before it lands.",
  description:
    "FutureCheck is a personal finance tracker built around biweekly pay periods. Bills, credit cards, loans, subscriptions and installment plans, stored on your device with optional end-to-end encrypted sync through your own cloud drive.",
  baseUrl: resolveBaseUrl(),
  allowIndexing: !isNonProductionDeployment,
  repoUrl: REPO_URL,
  issuesUrl: ISSUES_URL,
  nav: [
    { label: "Pay periods", href: "/#pay-periods" },
    { label: "Features", href: "/#features" },
    { label: "Privacy", href: "/#privacy" },
    { label: "Platforms", href: "/#platforms" },
    { label: "FAQ", href: "/#faq" },
  ],
  footer: [
    {
      title: "Product",
      links: [
        { label: "Features", href: "/#features" },
        { label: "Platforms", href: "/#platforms" },
        { label: "FAQ", href: "/#faq" },
      ],
    },
    {
      title: "Legal",
      links: [
        { label: "Privacy policy", href: "/privacy" },
        { label: "Terms of use", href: "/terms" },
      ],
    },
    {
      title: "Project",
      links: [
        { label: "Source code", href: REPO_URL, external: true },
        { label: "Report an issue", href: ISSUES_URL, external: true },
      ],
    },
  ],
} as const satisfies {
  name: string;
  tagline: string;
  description: string;
  baseUrl: string;
  allowIndexing: boolean;
  repoUrl: string;
  issuesUrl: string;
  nav: readonly NavLink[];
  footer: readonly FooterGroup[];
};

/**
 * Robots metadata for pages that should appear in search results. Set per page rather than in the
 * root layout, so it doesn't leak into the 404 page (which Next.js marks noindex on its own).
 */
export const indexableRobots = siteConfig.allowIndexing
  ? { index: true, follow: true }
  : { index: false, follow: false };

/** Absolute URL for a site path, for metadata, the sitemap and robots.txt. */
export function absoluteUrl(pathname = "/"): string {
  return new URL(pathname, siteConfig.baseUrl).toString();
}
