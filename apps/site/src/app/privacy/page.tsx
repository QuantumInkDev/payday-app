import type { Metadata } from "next";
import { Container } from "@/components/container";
import { Markdown } from "@/components/markdown";
import { loadPrivacyPolicy } from "@/lib/privacy-policy";
import { indexableRobots, siteConfig } from "@/site.config";

export const metadata: Metadata = {
  title: "Privacy policy",
  description: `How ${siteConfig.name} handles your data: stored on your device, optional end-to-end encrypted sync through your own cloud drive, and no analytics or tracking.`,
  alternates: { canonical: "/privacy" },
  robots: indexableRobots,
};

export default async function PrivacyPage() {
  const policy = await loadPrivacyPolicy();
  return (
    <Container className="py-16 sm:py-24">
      <article className="max-w-3xl">
        <Markdown source={policy} />
      </article>
    </Container>
  );
}
