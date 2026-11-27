import type { ReactNode } from "react";

type ContainerProps = {
  children: ReactNode;
  className?: string;
};

/** Centers content at the brand's content width with consistent side padding. */
export function Container({ children, className }: ContainerProps) {
  return (
    <div className={["mx-auto w-full max-w-content px-6", className].filter(Boolean).join(" ")}>
      {children}
    </div>
  );
}
