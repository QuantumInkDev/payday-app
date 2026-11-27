import type { Metadata } from "next";
import Link from "next/link";
import { Container } from "@/components/container";
import { Section } from "@/components/section";
import { indexableRobots, siteConfig } from "@/site.config";

// Placeholder landing page. Section order and copy follow the site plan
// (planning/2026-11-27-site-and-monorepo.md); layout and imagery come from the designer.

export const metadata: Metadata = {
  alternates: { canonical: "/" },
  robots: indexableRobots,
};

const features = [
  {
    title: "Bills and subscriptions",
    body: "Every recurring bill and subscription, with its amount, due day and whether it pays itself.",
  },
  {
    title: "Credit cards and loans",
    body: "Track what is left on each balance. Marking a payment paid brings the remaining balance down.",
  },
  {
    title: "Installment plans",
    body: "Buy-now-pay-later and other fixed plans, counted down payment by payment.",
  },
  {
    title: "Dashboard",
    body: "What is due this pay period, what is paid, and what is left, at a glance.",
  },
  {
    title: "Insights",
    body: "Charts of your balances over time, built from snapshots you choose to save.",
  },
  {
    title: "Payoff tracker",
    body: "Compare the snowball and avalanche methods and see which debt to pay down next.",
  },
] as const;

const privacyPoints = [
  {
    title: "Local-first",
    body: "Your data lives in a database on your device. FutureCheck works fully offline.",
  },
  {
    title: "No accounts, no servers",
    body: "There is nothing to sign up for, and FutureCheck has no servers of its own.",
  },
  {
    title: "Your drive, end-to-end encrypted",
    body: "Optional sync goes through your own Google Drive or OneDrive. Data is encrypted on your device first, with a recovery phrase only you hold.",
  },
  {
    title: "No bank linking",
    body: "FutureCheck never connects to your bank and doesn't import transactions. You stay in charge of what goes in.",
  },
] as const;

const platforms = [
  { name: "Windows", status: "Now" },
  { name: "macOS", status: "Coming" },
  { name: "iOS", status: "Coming" },
  { name: "Android", status: "Coming" },
  { name: "Web", status: "Coming" },
] as const;

const faqs = [
  {
    question: "Does FutureCheck connect to my bank?",
    answer:
      "No. FutureCheck doesn't link to banks and doesn't import transactions. You add your bills, cards, loans and plans yourself.",
  },
  {
    question: "Do I need an account?",
    answer:
      "No. There is no FutureCheck account. If you turn on sync, you sign in to your own cloud drive instead.",
  },
  {
    question: "Can anyone else read my data?",
    answer:
      "Synced data is encrypted on your device before it leaves. Your cloud provider can't read it, and neither can we.",
  },
  {
    question: "What if I lose a device?",
    answer:
      "Your recovery phrase unlocks your synced data on a new device. Keep it somewhere safe; nobody else holds a copy.",
  },
  {
    question: "Does it work with Notion?",
    answer:
      "Yes, optionally. The Notion connector sends the bills, payments and balance snapshots you choose to your own Notion workspace.",
  },
] as const;

export default function HomePage() {
  return (
    <>
      <section aria-labelledby="hero-heading" className="py-24 sm:py-32">
        <Container>
          <div className="max-w-3xl">
            <p className="text-sm font-semibold tracking-wide text-accent uppercase">
              Personal finance, one paycheck at a time
            </p>
            <h1
              id="hero-heading"
              className="mt-3 text-4xl font-semibold tracking-tight text-balance sm:text-6xl"
            >
              {siteConfig.tagline}
            </h1>
            <p className="mt-6 text-lg text-muted sm:text-xl">
              {siteConfig.name} lines up your bills, credit cards, loans, subscriptions and
              installment plans with the biweekly pay periods that cover them. Private by design:
              your data stays on your devices.
            </p>
            <div className="mt-10 flex flex-wrap gap-4">
              <Link
                href="/#features"
                className="rounded-md bg-accent px-5 py-3 font-semibold text-accent-contrast hover:opacity-90"
              >
                See the features
              </Link>
              <Link
                href="/privacy"
                className="rounded-md border border-border px-5 py-3 font-semibold hover:bg-surface"
              >
                Read the privacy policy
              </Link>
            </div>
          </div>
        </Container>
      </section>

      <Section
        id="pay-periods"
        eyebrow="Pay periods"
        title="Built around the way you get paid"
        tone="surface"
        intro={
          <p>
            Most budgets think in months. Paychecks arrive every two weeks. {siteConfig.name} puts
            each bill in the pay period that pays it, so you know what this paycheck has to cover
            and what is left once it does.
          </p>
        }
      />

      <Section id="features" eyebrow="Features" title="Everything you owe, in one place">
        <ul className="grid gap-6 sm:grid-cols-2 lg:grid-cols-3">
          {features.map((feature) => (
            <li key={feature.title} className="rounded-lg border border-border p-6">
              <h3 className="text-lg font-semibold">{feature.title}</h3>
              <p className="mt-2 text-muted">{feature.body}</p>
            </li>
          ))}
        </ul>
      </Section>

      <Section
        id="privacy"
        eyebrow="Privacy"
        title="Your numbers stay yours"
        tone="surface"
        intro={
          <p>
            No analytics, tracking or advertising. Read the full{" "}
            <Link href="/privacy" className="text-accent underline underline-offset-4">
              privacy policy
            </Link>
            .
          </p>
        }
      >
        <ul className="grid gap-6 sm:grid-cols-2">
          {privacyPoints.map((point) => (
            <li key={point.title} className="rounded-lg bg-bg p-6">
              <h3 className="text-lg font-semibold">{point.title}</h3>
              <p className="mt-2 text-muted">{point.body}</p>
            </li>
          ))}
        </ul>
      </Section>

      <Section
        id="platforms"
        eyebrow="Platforms"
        title="Every device you use"
        intro={<p>Windows first, with the same data on each device as the other apps arrive.</p>}
      >
        <ul className="grid grid-cols-2 gap-4 sm:grid-cols-5">
          {platforms.map((platform) => (
            <li key={platform.name} className="rounded-lg border border-border p-5 text-center">
              <p className="font-semibold">{platform.name}</p>
              <p className="mt-1 text-sm text-muted">{platform.status}</p>
            </li>
          ))}
        </ul>
      </Section>

      <Section id="faq" eyebrow="FAQ" title="Questions, answered" tone="surface">
        <div className="max-w-3xl divide-y divide-border rounded-lg border border-border bg-bg">
          {faqs.map((faq) => (
            <details key={faq.question} className="group p-6">
              <summary className="cursor-pointer list-none font-semibold marker:hidden">
                {faq.question}
              </summary>
              <p className="mt-3 text-muted">{faq.answer}</p>
            </details>
          ))}
        </div>
      </Section>
    </>
  );
}
