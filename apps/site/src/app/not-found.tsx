import type { Metadata } from "next";
import Link from "next/link";
import { Container } from "@/components/container";

// Next.js adds a noindex robots tag to this page on its own.
export const metadata: Metadata = {
  title: "Page not found",
};

export default function NotFound() {
  return (
    <Container className="py-24 sm:py-32">
      <div className="max-w-xl">
        <p className="text-sm font-semibold tracking-wide text-accent uppercase">404</p>
        <h1 className="mt-3 text-4xl font-semibold tracking-tight">Page not found</h1>
        <p className="mt-4 text-lg text-muted">
          That page doesn&apos;t exist, or it has moved.
        </p>
        <Link
          href="/"
          className="mt-8 inline-block rounded-md bg-accent px-5 py-3 font-semibold text-accent-contrast hover:opacity-90"
        >
          Back to the home page
        </Link>
      </div>
    </Container>
  );
}
