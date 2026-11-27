import Link from "next/link";
import type { ReactNode } from "react";
import type { NavLink } from "@/site.config";

type SmartLinkProps = {
  link: NavLink;
  className?: string;
  children?: ReactNode;
};

/** Renders a site link: next/link for internal paths, a plain anchor for external URLs. */
export function SmartLink({ link, className, children }: SmartLinkProps) {
  const content = children ?? link.label;
  if (link.external) {
    return (
      <a href={link.href} className={className} rel="noopener noreferrer">
        {content}
      </a>
    );
  }
  return (
    <Link href={link.href} className={className}>
      {content}
    </Link>
  );
}
