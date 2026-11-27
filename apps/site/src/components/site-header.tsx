import Link from "next/link";
import { Container } from "@/components/container";
import { SmartLink } from "@/components/smart-link";
import { siteConfig } from "@/site.config";

export function SiteHeader() {
  return (
    <header className="sticky top-0 z-40 border-b border-border bg-bg/90 backdrop-blur">
      <Container className="flex h-16 items-center justify-between gap-6">
        <Link href="/" className="text-lg font-semibold tracking-tight">
          {siteConfig.name}
        </Link>
        <nav aria-label="Main">
          <ul className="hidden items-center gap-6 text-sm text-muted md:flex">
            {siteConfig.nav.map((link) => (
              <li key={link.href}>
                <SmartLink link={link} className="hover:text-fg" />
              </li>
            ))}
          </ul>
        </nav>
      </Container>
    </header>
  );
}
