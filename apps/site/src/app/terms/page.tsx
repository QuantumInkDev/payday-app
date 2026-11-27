import type { Metadata } from "next";
import Link from "next/link";
import { Container } from "@/components/container";
import { siteConfig } from "@/site.config";

// PLACEHOLDER. The terms of use are not written yet. This page is kept out of search results and
// out of the sitemap until the real terms replace it.

export const metadata: Metadata = {
  title: "Terms of use",
  description: `The ${siteConfig.name} terms of use (placeholder; not yet published).`,
  alternates: { canonical: "/terms" },
  robots: { index: false, follow: true },
};

export default function TermsPage() {
  return (
    <Container className="py-16 sm:py-24">
      <article className="max-w-3xl">
        <p
          role="note"
          className="rounded-md border border-accent bg-surface px-4 py-3 text-sm font-semibold"
        >
          Placeholder: the terms of use have not been written yet. Nothing on this page is in
          effect.
        </p>
        <h1 className="mt-10 text-4xl font-semibold tracking-tight sm:text-5xl">Terms of use</h1>
        <p className="mt-6 leading-7 text-muted">
          The {siteConfig.name} terms of use will be published here. Until then, see the{" "}
          <Link href="/privacy" className="text-accent underline underline-offset-4">
            privacy policy
          </Link>{" "}
          for how {siteConfig.name} handles your data, or{" "}
          <a
            href={siteConfig.issuesUrl}
            className="text-accent underline underline-offset-4"
            rel="noopener noreferrer"
          >
            open an issue
          </a>{" "}
          with any questions.
        </p>
      </article>
    </Container>
  );
}
