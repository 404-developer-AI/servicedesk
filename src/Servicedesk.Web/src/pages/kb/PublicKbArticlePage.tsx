import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertCircle, Loader2 } from "lucide-react";
import { publicKbApi } from "@/lib/kb-api";
import { SafeHtml } from "@/components/SafeHtml";

/// Bare-layout page rendered at `/kb/public/:articleId` — the target of
/// the public links agents insert into outbound mail. The customer has no
/// session; the server serves Published articles only and 404s everything
/// else (including the whole feature when the admin toggle is off), so a
/// revoked or never-published article shows the same neutral state as a
/// guessed id. Same purple/blue glass shell as the intake/survey pages.
export function PublicKbArticlePage({ articleId }: { articleId: string }) {
  // These links are for the customers who received them, not for crawlers
  // that picked one up from a forwarded mail. The API responses carry
  // X-Robots-Tag; this covers the HTML document itself.
  useEffect(() => {
    const meta = document.createElement("meta");
    meta.name = "robots";
    meta.content = "noindex, nofollow";
    document.head.appendChild(meta);
    return () => meta.remove();
  }, []);

  const articleQ = useQuery({
    queryKey: ["public-kb-article", articleId],
    queryFn: () => publicKbApi.get(articleId),
    retry: false,
  });

  if (articleQ.isLoading) {
    return (
      <PublicShell>
        <div className="flex items-center gap-3 text-muted-foreground">
          <Loader2 className="h-5 w-5 animate-spin" />
          <span>Loading…</span>
        </div>
      </PublicShell>
    );
  }

  if (articleQ.isError || !articleQ.data) {
    return (
      <PublicShell>
        <div className="flex flex-col items-start gap-3">
          <AlertCircle className="h-6 w-6 text-muted-foreground" />
          <p className="text-sm text-muted-foreground">
            This article is not available.
          </p>
        </div>
      </PublicShell>
    );
  }

  const article = articleQ.data;
  return (
    <PublicShell>
      <article className="flex flex-col gap-5">
        <h1 className="font-display text-display-md font-semibold text-foreground">
          {article.title}
        </h1>
        {article.bodyHtml.length > 0 ? (
          <SafeHtml html={article.bodyHtml} />
        ) : (
          <p className="text-sm italic text-muted-foreground">
            This article has no content.
          </p>
        )}
      </article>
    </PublicShell>
  );
}

/// Wider than the survey/intake shells — articles are long-form reading.
function PublicShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="app-background min-h-screen w-full px-4 py-12">
      <div className="mx-auto max-w-3xl rounded-2xl border border-glass-strong bg-glass p-8 shadow-2xl backdrop-blur-xl">
        {children}
      </div>
    </div>
  );
}
