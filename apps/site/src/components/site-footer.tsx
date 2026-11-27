import { Container } from "@/components/container";
import { SmartLink } from "@/components/smart-link";
import { siteConfig } from "@/site.config";

export function SiteFooter() {
  return (
    <footer className="border-t border-border bg-surface">
      <Container className="grid gap-10 py-12 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <p className="font-semibold">{siteConfig.name}</p>
          <p className="mt-2 text-sm text-muted">{siteConfig.tagline}</p>
        </div>
        {siteConfig.footer.map((group) => (
          <nav key={group.title} aria-label={group.title}>
            <p className="text-sm font-semibold">{group.title}</p>
            <ul className="mt-3 space-y-2 text-sm text-muted">
              {group.links.map((link) => (
                <li key={link.href}>
                  <SmartLink link={link} className="hover:text-fg" />
                </li>
              ))}
            </ul>
          </nav>
        ))}
      </Container>
      <Container className="border-t border-border py-6 text-xs text-muted">
        <p>
          {siteConfig.name} keeps your data on your devices. No accounts, no servers, no tracking.
        </p>
      </Container>
    </footer>
  );
}
