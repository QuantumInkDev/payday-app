import Link from "next/link";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";

const components: Components = {
  h1: ({ children }) => (
    <h1 className="text-4xl font-semibold tracking-tight sm:text-5xl">{children}</h1>
  ),
  h2: ({ children }) => (
    <h2 className="mt-12 text-2xl font-semibold tracking-tight">{children}</h2>
  ),
  h3: ({ children }) => <h3 className="mt-8 text-xl font-semibold">{children}</h3>,
  p: ({ children }) => <p className="mt-4 leading-7">{children}</p>,
  ul: ({ children }) => <ul className="mt-4 list-disc space-y-2 pl-6 leading-7">{children}</ul>,
  ol: ({ children }) => (
    <ol className="mt-4 list-decimal space-y-2 pl-6 leading-7">{children}</ol>
  ),
  strong: ({ children }) => <strong className="font-semibold">{children}</strong>,
  code: ({ children }) => (
    <code className="rounded-sm bg-surface px-1.5 py-0.5 font-mono text-[0.9em]">{children}</code>
  ),
  a: ({ href, children }) => {
    const className = "text-accent underline underline-offset-4";
    if (href?.startsWith("/")) {
      return (
        <Link href={href} className={className}>
          {children}
        </Link>
      );
    }
    return (
      <a href={href} className={className} rel="noopener noreferrer">
        {children}
      </a>
    );
  },
};

type MarkdownProps = {
  source: string;
};

/** Renders trusted, repo-owned Markdown (such as PRIVACY.md) with the site's typography. */
export function Markdown({ source }: MarkdownProps) {
  return (
    <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
      {source}
    </ReactMarkdown>
  );
}
