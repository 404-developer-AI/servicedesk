import { useMemo } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import {
  ChevronRight,
  Pencil,
  Star,
  StarOff,
  Trash2,
  ArrowUpRight,
  ArrowDown,
} from "lucide-react";
import { toast } from "sonner";
import {
  kbApi,
  type KbArticleStatus,
  type KbSectionNode,
} from "@/lib/kb-api";
import { useCurrentRole } from "@/hooks/useCurrentRole";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { SafeHtml } from "@/components/SafeHtml";
import { StatusPill } from "@/pages/kb/KbHomePage";

type Props = { articleId: string };

/// Article reader: title + status pill + sanitized body + side-panel with
/// metadata, status flips and admin-only featured toggle. Edit jumps to
/// /kb/articles/:id/edit. Status flips that the role can perform are
/// rendered inline; everything else is hidden (the API enforces the same
/// gate server-side as a defence-in-depth backstop).
export function KbArticlePage({ articleId }: Props) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const role = useCurrentRole();
  const isAdmin = role === "Admin";

  const { data, isLoading } = useQuery({
    queryKey: ["kb", "article", articleId, "body"],
    queryFn: () => kbApi.getArticle(articleId, true),
  });

  const { data: tree } = useQuery({
    queryKey: ["kb", "sections"],
    queryFn: kbApi.listSections,
  });

  const breadcrumb = useMemo(() => {
    if (!data?.article || !tree) return [];
    return buildBreadcrumb(tree.tree, data.article.sectionId);
  }, [data, tree]);

  const flip = useMutation({
    mutationFn: (target: KbArticleStatus) => kbApi.changeStatus(articleId, target),
    onSuccess: (updated) => {
      toast.success(`Article is now ${updated.status}.`);
      queryClient.invalidateQueries({ queryKey: ["kb"] });
    },
    onError: () => toast.error("Could not change status."),
  });

  const featured = useMutation({
    mutationFn: (next: boolean) => kbApi.setFeatured(articleId, next),
    onSuccess: () => {
      toast.success("Featured updated.");
      queryClient.invalidateQueries({ queryKey: ["kb"] });
    },
    onError: () => toast.error("Could not update featured."),
  });

  const archive = useMutation({
    mutationFn: () => kbApi.deleteArticle(articleId, false),
    onSuccess: () => {
      toast.success("Article archived.");
      queryClient.invalidateQueries({ queryKey: ["kb"] });
      navigate({ to: "/kb" });
    },
    onError: () => toast.error("Could not archive article."),
  });

  if (isLoading || !data?.article) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-8 w-96" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }

  const { article, translation } = data;
  const title = translation?.title ?? article.slug;

  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1fr)_320px]">
      <div className="flex flex-col gap-5">
        <header className="space-y-2">
          <nav className="flex flex-wrap items-center gap-1 text-xs text-muted-foreground">
            <Link to="/kb" className="hover:text-foreground">Knowledge Base</Link>
            {breadcrumb.map((b) => (
              <span key={b.id} className="flex items-center gap-1">
                <ChevronRight className="h-3 w-3" />
                <Link
                  to="/kb/sections/$sectionId"
                  params={{ sectionId: b.id }}
                  className="hover:text-foreground"
                >
                  {b.title}
                </Link>
              </span>
            ))}
          </nav>
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="font-display text-display-md font-semibold text-foreground">
              {title}
            </h1>
            <StatusPill status={article.status} />
            {article.isFeatured && (
              <span className="inline-flex items-center gap-1 rounded-full border border-amber-300/30 bg-amber-300/10 px-2 py-0.5 text-[10px] uppercase tracking-wider text-amber-200">
                <Star className="h-3 w-3" /> Featured
              </span>
            )}
          </div>
        </header>

        <article className="glass-card p-6">
          {translation && translation.bodyHtml.length > 0 ? (
            <SafeHtml html={translation.bodyHtml} />
          ) : (
            <p className="text-sm italic text-muted-foreground">
              This article has no body yet.
            </p>
          )}
        </article>

        {article.editorNotes && (article.status === "Draft" || article.status === "Internal" || isAdmin) && (
          <aside className="glass-card border-amber-300/20 p-4 text-sm">
            <div className="mb-1 text-[10px] uppercase tracking-wider text-amber-200/80">
              Editor notes
            </div>
            <div className="whitespace-pre-wrap text-muted-foreground">
              {article.editorNotes}
            </div>
          </aside>
        )}
      </div>

      <aside className="flex flex-col gap-4">
        <div className="glass-card flex flex-col gap-3 p-5">
          <Button
            onClick={() => navigate({ to: "/kb/articles/$articleId/edit", params: { articleId } })}
            className="gap-1"
          >
            <Pencil className="h-4 w-4" /> Edit article
          </Button>
          <div className="grid grid-cols-2 gap-2">
            <StatusFlipButton
              label="To Draft"
              icon={<ArrowDown className="h-3.5 w-3.5" />}
              disabled={article.status === "Draft"}
              visible={article.status === "Internal" || isAdmin}
              onClick={() => flip.mutate("Draft")}
            />
            <StatusFlipButton
              label="To Internal"
              icon={<ArrowUpRight className="h-3.5 w-3.5" />}
              disabled={article.status === "Internal"}
              visible={article.status === "Draft" || isAdmin}
              onClick={() => flip.mutate("Internal")}
            />
            {isAdmin && (
              <StatusFlipButton
                label="Publish"
                icon={<ArrowUpRight className="h-3.5 w-3.5" />}
                disabled={article.status === "Published"}
                visible
                onClick={() => flip.mutate("Published")}
              />
            )}
            {isAdmin && (
              <StatusFlipButton
                label="Archive"
                icon={<Trash2 className="h-3.5 w-3.5" />}
                disabled={article.status === "Archived"}
                visible
                onClick={() => archive.mutate()}
              />
            )}
          </div>
          {isAdmin && (
            <button
              type="button"
              onClick={() => featured.mutate(!article.isFeatured)}
              className="flex items-center justify-center gap-2 rounded-md border border-white/10 bg-white/[0.03] px-3 py-2 text-xs text-muted-foreground transition-colors hover:bg-white/[0.06] hover:text-foreground"
            >
              {article.isFeatured ? (
                <><StarOff className="h-3.5 w-3.5" /> Remove from featured</>
              ) : (
                <><Star className="h-3.5 w-3.5" /> Mark as featured</>
              )}
            </button>
          )}
        </div>

        <dl className="glass-card grid grid-cols-1 gap-3 p-5 text-xs">
          <Meta label="Slug" value={article.slug} mono />
          <Meta label="Created" value={new Date(article.createdUtc).toLocaleString()} />
          <Meta label="Updated" value={new Date(article.updatedUtc).toLocaleString()} />
          <Meta
            label="Status changed"
            value={new Date(article.lastStatusChangedUtc).toLocaleString()}
          />
        </dl>
      </aside>
    </div>
  );
}

function StatusFlipButton({
  label, icon, disabled, visible, onClick,
}: {
  label: string;
  icon: React.ReactNode;
  disabled: boolean;
  visible: boolean;
  onClick: () => void;
}) {
  if (!visible) return null;
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className="flex items-center justify-center gap-1.5 rounded-md border border-white/10 bg-white/[0.03] px-3 py-2 text-xs text-muted-foreground transition-colors hover:bg-white/[0.06] hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
    >
      {icon} {label}
    </button>
  );
}

function Meta({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex justify-between gap-2">
      <dt className="text-muted-foreground/70">{label}</dt>
      <dd className={mono ? "font-mono text-foreground/80" : "text-foreground/80"}>{value}</dd>
    </div>
  );
}

function buildBreadcrumb(nodes: KbSectionNode[], targetId: string): KbSectionNode[] {
  function walk(stack: KbSectionNode[], list: KbSectionNode[]): KbSectionNode[] | null {
    for (const n of list) {
      const next = [...stack, n];
      if (n.id === targetId) return next;
      const deeper = walk(next, n.children);
      if (deeper) return deeper;
    }
    return null;
  }
  return walk([], nodes) ?? [];
}
