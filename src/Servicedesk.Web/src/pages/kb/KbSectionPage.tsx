import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { ChevronRight, Plus } from "lucide-react";
import { kbApi, type KbArticleStatus } from "@/lib/kb-api";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { StatusPill } from "@/pages/kb/KbHomePage";

type Props = { sectionId: string };

/// Section detail: lists articles in this section + child sections + a
/// "+ New article" button. Status filter / search are URL-state-free in
/// v0.0.31 — quick listing with a fixed pageSize is enough for a typical
/// KB; pagination + filters land alongside the admin UI in v0.0.32+.
export function KbSectionPage({ sectionId }: Props) {
  const navigate = useNavigate();
  const [statusFilter, setStatusFilter] = useState<KbArticleStatus | "All">("All");

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
              className="rounded-lg border border-white/10 bg-white/[0.03] px-3 py-1.5 text-xs text-muted-foreground transition-colors hover:border-white/20 hover:bg-white/[0.06] hover:text-foreground"
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
                  : "border-white/10 bg-white/[0.03] text-muted-foreground hover:border-white/20 hover:text-foreground")
              }
            >
              {s}
            </button>
          ))}
        </div>
        {articles && (
          <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
            {articles.totalHits} article{articles.totalHits === 1 ? "" : "s"}
          </Badge>
        )}
      </div>

      {articlesLoading ? (
        <Skeleton className="h-40 w-full" />
      ) : articles && articles.items.length > 0 ? (
        <section className="glass-card overflow-hidden">
          <table className="w-full text-left text-sm">
            <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-white/10">
              <tr>
                <th className="px-4 py-3 font-medium">Title</th>
                <th className="px-4 py-3 font-medium">Status</th>
                <th className="px-4 py-3 font-medium">Featured</th>
                <th className="px-4 py-3 font-medium">Last updated</th>
              </tr>
            </thead>
            <tbody>
              {articles.items.map((a) => (
                <tr
                  key={a.id}
                  className="border-b border-white/5 last:border-b-0 hover:bg-white/[0.03]"
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
                    {new Date(a.updatedUtc).toLocaleString()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      ) : (
        <div className="rounded-lg border border-dashed border-white/10 px-6 py-12 text-center text-sm text-muted-foreground">
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
