import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { ChevronRight, Plus } from "lucide-react";
import { kbApi, type KbArticleStatus } from "@/lib/kb-api";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { StatusPill } from "@/pages/kb/KbHomePage";
import { InPageSearchProvider, useInPageSearch } from "@/components/InPageSearch";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";

type Props = { sectionId: string };

/// Section detail: lists articles in this section + child sections + a
/// "+ New article" button. Status filter / search are URL-state-free in
/// v0.0.31 — quick listing with a fixed pageSize is enough for a typical
/// KB; pagination + filters land alongside the admin UI in v0.0.32+.
/// Ctrl+F opens the shared in-page search bar (same as the ticket view):
/// Filter hides non-matching article rows, Highlight marks matches.
export function KbSectionPage({ sectionId }: Props) {
  return (
    <InPageSearchProvider placeholder="Search in this section…">
      <SectionContent sectionId={sectionId} />
    </InPageSearchProvider>
  );
}

function SectionContent({ sectionId }: Props) {
  const navigate = useNavigate();
  const [statusFilter, setStatusFilter] = useState<KbArticleStatus | "All">("All");
  const { matchesText, mode, query, registerScope } = useInPageSearch();
  const { time: serverTime } = useServerTime();
  const offset = serverTime?.offsetMinutes ?? 0;

  const { data: section, isLoading: sectionLoading } = useQuery({
    queryKey: ["kb", "section", sectionId],
    queryFn: () => kbApi.getSection(sectionId),
  });

  const { data: tree } = useQuery({
    queryKey: ["kb", "sections"],
    queryFn: kbApi.listSections,
  });

  const { data: articles, isLoading: articlesLoading } = useQuery({
    queryKey: ["kb", "articles", sectionId, statusFilter],
    queryFn: () =>
      kbApi.listArticles({
        sectionId,
        status: statusFilter === "All" ? undefined : statusFilter,
        pageSize: 100,
      }),
  });

  const breadcrumb = useMemo(
    () => buildBreadcrumb(tree?.tree ?? [], sectionId),
    [tree, sectionId],
  );

  const childNode = useMemo(
    () => findNode(tree?.tree ?? [], sectionId),
    [tree, sectionId],
  );

  // Filter mode hides rows that don't match the Ctrl+F query; Highlight
  // mode keeps every row and the provider marks matches in the DOM.
  const visibleArticles = useMemo(() => {
    const items = articles?.items ?? [];
    if (mode !== "filter" || !query.trim()) return items;
    return items.filter((a) => matchesText(a.title));
  }, [articles, mode, query, matchesText]);

  if (sectionLoading || !section) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-32 w-full" />
      </div>
    );
  }

  return (
    <div className="flex min-h-[calc(100vh-8rem)] w-full flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <nav className="flex items-center gap-1 text-xs text-muted-foreground">
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
          <h1 className="font-display text-display-md font-semibold text-foreground">
            {childNode?.title ?? section.translations[0]?.title ?? section.section.slug}
          </h1>
          {childNode?.description && (
            <p className="max-w-2xl text-sm text-muted-foreground">{childNode.description}</p>
          )}
        </div>
        <Button
          onClick={() => navigate({ to: "/kb/articles/new", search: { sectionId } })}
          className="gap-1"
        >
          <Plus className="h-4 w-4" />
          New article
        </Button>
      </header>

      {childNode && childNode.children.length > 0 && (
        <section className="glass-card flex flex-wrap gap-2 p-4">
          {childNode.children.map((child) => (
            <Link
              key={child.id}
              to="/kb/sections/$sectionId"
              params={{ sectionId: child.id }}
              className="rounded-lg border border-glass bg-glass px-3 py-1.5 text-xs text-muted-foreground transition-colors hover:border-glass-strong hover:bg-glass-hover hover:text-foreground"
            >
              {child.title}
            </Link>
          ))}
        </section>
      )}

      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          <span>Status:</span>
          {(["All", "Draft", "Internal", "Published", "Archived"] as const).map((s) => (
            <button
              key={s}
              onClick={() => setStatusFilter(s)}
              className={
                "rounded-full border px-2 py-0.5 text-[10px] uppercase tracking-wider transition-colors " +
                (statusFilter === s
                  ? "border-primary/40 bg-primary/15 text-foreground"
                  : "border-glass bg-glass text-muted-foreground hover:border-glass-strong hover:text-foreground")
              }
            >
              {s}
            </button>
          ))}
        </div>
        {articles && (
          <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
            {visibleArticles.length !== articles.items.length
              ? `${visibleArticles.length} / ${articles.totalHits} articles`
              : `${articles.totalHits} article${articles.totalHits === 1 ? "" : "s"}`}
          </Badge>
        )}
      </div>

      {articlesLoading ? (
        <Skeleton className="h-40 w-full" />
      ) : articles && articles.items.length > 0 ? (
        // shrink-0: as a flex child of the page column this card would
        // otherwise be squeezed to the leftover viewport height (its
        // overflow-hidden zeroes the automatic min-size) and the clipped
        // rows could never be scrolled to.
        <section className="glass-card shrink-0 overflow-hidden" ref={registerScope}>
          <table className="w-full text-left text-sm">
            <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
              <tr>
                <th className="px-4 py-3 font-medium">Title</th>
                <th className="px-4 py-3 font-medium">Status</th>
                <th className="px-4 py-3 font-medium">Featured</th>
                <th className="px-4 py-3 font-medium">Last updated</th>
              </tr>
            </thead>
            <tbody>
              {visibleArticles.length === 0 && (
                <tr>
                  <td colSpan={4} className="px-4 py-8 text-center text-sm text-muted-foreground">
                    No articles match the search.
                  </td>
                </tr>
              )}
              {visibleArticles.map((a) => (
                <tr
                  key={a.id}
                  className="border-b border-glass last:border-b-0 hover:bg-glass-hover"
                >
                  <td className="px-4 py-3">
                    <Link
                      to="/kb/articles/$articleId"
                      params={{ articleId: a.id }}
                      className="text-foreground hover:underline"
                    >
                      {a.title}
                    </Link>
                  </td>
                  <td className="px-4 py-3">
                    <StatusPill status={a.status} />
                  </td>
                  <td className="px-4 py-3 text-muted-foreground">
                    {a.isFeatured ? "★" : "—"}
                  </td>
                  <td className="px-4 py-3 text-xs text-muted-foreground">
                    {toServerLocal(a.updatedUtc, offset)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      ) : (
        <div className="rounded-lg border border-dashed border-glass px-6 py-12 text-center text-sm text-muted-foreground">
          No articles in this section yet.{" "}
          <button
            type="button"
            onClick={() => navigate({ to: "/kb/articles/new", search: { sectionId } })}
            className="text-primary hover:underline"
          >
            Create the first one
          </button>.
        </div>
      )}
    </div>
  );
}

type TreeNode = {
  id: string;
  parentSectionId: string | null;
  slug: string;
  iconName: string | null;
  position: number;
  title: string;
  description: string | null;
  children: TreeNode[];
};

function findNode(nodes: TreeNode[], id: string): TreeNode | undefined {
  for (const n of nodes) {
    if (n.id === id) return n;
    const found = findNode(n.children, id);
    if (found) return found;
  }
  return undefined;
}

function buildBreadcrumb(nodes: TreeNode[], targetId: string): TreeNode[] {
  function walk(stack: TreeNode[], list: TreeNode[]): TreeNode[] | null {
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
