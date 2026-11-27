import type { ReactNode } from "react";
import { Container } from "@/components/container";

type SectionProps = {
  id: string;
  eyebrow?: string;
  title: string;
  intro?: ReactNode;
  children?: ReactNode;
  tone?: "default" | "surface";
};

/** A landing-page section with an anchor id, so the header nav can link to it. */
export function Section({ id, eyebrow, title, intro, children, tone = "default" }: SectionProps) {
  const headingId = `${id}-heading`;
  return (
    <section
      id={id}
      aria-labelledby={headingId}
      className={tone === "surface" ? "bg-surface py-20" : "py-20"}
    >
      <Container>
        <div className="max-w-2xl">
          {eyebrow ? (
            <p className="text-sm font-semibold tracking-wide text-accent uppercase">{eyebrow}</p>
          ) : null}
          <h2 id={headingId} className="mt-2 text-3xl font-semibold tracking-tight sm:text-4xl">
            {title}
          </h2>
          {intro ? <div className="mt-4 text-lg text-muted">{intro}</div> : null}
        </div>
        {children ? <div className="mt-10">{children}</div> : null}
      </Container>
    </section>
  );
}
