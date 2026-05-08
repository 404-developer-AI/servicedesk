import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { BookOpen, FolderTree, Search as SearchIcon, Star } from "lucide-react";
import { kbApi, type KbSectionNode, type KbArticleListItem } from "@/lib/kb-api";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

/// Landing page for the standalone KB. Three regions: search bar at the top,
/// section tree on the left, featured + recent on the right. The search box
/// is a thin client wrapper — typing more than two characters routes to the
/// global /search page with type=kb-articles so the results live in the
/// canonical search surface (KbArticleSearchSource powers the rows).
export function KbHomePage() {
  const navigate = useNavigate();
  const [query, setQuery] = useState("");

  const { data: tree, isLoading: treeLoading } = useQuery({
    queryKey: ["kb", "sections"],
    queryFn: kbApi.listSections,
  });

  const { data: featured, isLoading: featuredLoading } = useQuery({
    queryKey: ["kb", "featured"],
    queryFn: () => kbApi.listFeatured(6),
  });

  const { data: recent, isLoading: recentLoading } = useQuery({
    queryKey: ["kb", "recent"],
    queryFn: () =>
      kbApi.listArticles({ pageSize: 8 }),
  });

  const onSearchSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (query.trim().length === 0) return;
    navigate({
      to: "/search",
      search: { q: query.trim(), type: "kb-articles", offset: undefined },
    });
  };

  return (
    <div className="flex min-h-[calc(100vh-8rem)] w-full flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="font-display text-display-md font-semibold text-foreground">
            Knowledge Base
          </h1>
          <p className="text-sm text-muted-foreground">
            Internal articles agents can browse, write, and link from tickets.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Agents only
        </Badge>
      </header>

      <form onSubmit={onSearchSubmit} className="glass-card flex items-center gap-3 p-4">
        <SearchIcon className="h-4 w-4 shrink-0 text-muted-foreground" />
        <Input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search the knowledge base…"
          className="h-9 border-none bg-transparent shadow-none focus-visible:ring-0"
          autoFocus
        />
        <button
          type="submit"
          className="rounded-md border border-white/10 bg-white/[0.05] px-3 py-1.5 text-xs uppercase tracking-wider text-muted-foreground transition-colors hover:bg-white/[0.08] hover:text-foreground"
        >
          Search
        </button>
      </form>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)]">
        <section className="glass-card flex flex-col gap-4 p-5">
          <div className="flex items-center gap-2 text-sm font-medium text-foreground">
            <FolderTree className="h-4 w-4 text-primary" /> Sections
          </div>
          {treeLoading ? (
            <SectionsSkeleton />
          ) : tree && tree.tree.length > 0 ? (
            <ul className="space-y-1">
              {tree.tree.map((node) => (
                <SectionTreeNode key={node.id} node={node} depth={0} />
              ))}
            </ul>
          ) : (
            <EmptyState
              icon={<FolderTree className="h-5 w-5" />}
              title="No sections yet"
              description="An admin can add a section in Settings → Knowledge Base."
            />
          )}
        </section>

        <div className="flex flex-col gap-6">
          <section className="glass-card flex flex-col gap-4 p-5">
            <div className="flex items-center gap-2 text-sm font-medium text-foreground">
              <Star className="h-4 w-4 text-amber-300" /> Featured
            </div>
            {featuredLoading ? (
              <Skeleton className="h-32" />
            ) : featured && featured.length > 0 ? (
              <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                {featured.map((f) => (
                  <li key={f.id}>
                    <Link
                      to="/kb/articles/$articleId"
                      params={{ articleId: f.id }}
                      className="block rounded-lg border border-white/5 bg-white/[0.02] p-3 transition-colors hover:border-white/15 hover:bg-white/[0.04]"
                    >
                      <div className="line-clamp-2 text-sm font-medium text-foreground">
                        {f.title}
                      </div>
                      <div className="mt-1 text-[11px] text-muted-foreground">
                        Updated {new Date(f.updatedUtc).toLocaleDateString()}
                      </div>
                    </Link>
                  </li>
                ))}
              </ul>
            ) : (
              <EmptyState
                icon={<Star className="h-5 w-5" />}
                title="No featured articles"
                description="An admin can mark articles as featured to surface them here."
              />
            )}
          </section>

          <section className="glass-card flex flex-col gap-4 p-5">
            <div className="flex items-center gap-2 text-sm font-medium text-foreground">
              <BookOpen className="h-4 w-4 text-primary" /> Recently updated
            </div>
            {recentLoading ? (
              <Skeleton className="h-40" />
            ) : recent && recent.items.length > 0 ? (
              <ul className="divide-y divide-white/5">
                {recent.items.map((a) => (
                  <RecentRow key={a.id} item={a} />
                ))}
              </ul>
            ) : (
              <EmptyState
                icon={<BookOpen className="h-5 w-5" />}
                title="No articles yet"
                description="Pick a section to start writing."
              />
            )}
          </section>
        </div>
      </div>
    </div>
  );
}

function SectionTreeNode({ node, depth }: { node: KbSectionNode; depth: number }) {
  const indent = useMemo(() => ({ paddingLeft: depth * 14 + 8 }), [depth]);
  return (
    <li>
      <Link
        to="/kb/sections/$sectionId"
        params={{ sectionId: node.id }}
        className="flex items-center gap-2 rounded-md px-2 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-white/[0.04] hover:text-foreground"
        style={indent}
      >
        <span className="truncate">{node.title}</span>
      </Link>
      {node.children.length > 0 && (
        <ul className="space-y-1">
          {node.children.map((child) => (
            <SectionTreeNode key={child.id} node={child} depth={depth + 1} />
          ))}
        </ul>
      )}
    </li>
  );
}

function RecentRow({ item }: { item: KbArticleListItem }) {
  return (
    <li className="py-2">
      <Link
        to="/kb/articles/$articleId"
        params={{ articleId: item.id }}
        className="flex items-center justify-between gap-3 rounded-md px-2 py-1.5 transition-colors hover:bg-white/[0.04]"
      >
        <div className="min-w-0">
          <div className="truncate text-sm text-foreground">{item.title}</div>
          <div className="text-[11px] text-muted-foreground">
            Updated {new Date(item.updatedUtc).toLocaleDateString()}
          </div>
        </div>
        <StatusPill status={item.status} />
      </Link>
    </li>
  );
}

export function StatusPill({ status }: { status: string }) {
  const styles: Record<string, string> = {
    Draft: "border-amber-300/30 bg-amber-300/10 text-amber-200",
    Internal: "border-sky-300/30 bg-sky-300/10 text-sky-200",
    Published: "border-emerald-300/30 bg-emerald-300/10 text-emerald-200",
    Archived: "border-white/10 bg-white/[0.04] text-muted-foreground",
  };
  return (
    <span
      className={cn(
        "shrink-0 rounded-full border px-2 py-0.5 text-[10px] uppercase tracking-wider",
        styles[status] ?? styles.Draft,
      )}
    >
      {status}
    </span>
  );
}

function SectionsSkeleton() {
  return (
    <div className="space-y-2">
      {[...Array(5)].map((_, i) => (
        <Skeleton key={i} className="h-7 w-full" />
      ))}
    </div>
  );
}

function EmptyState({ icon, title, description }: { icon: React.ReactNode; title: string; description: string }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 rounded-md border border-dashed border-white/10 px-4 py-8 text-center">
      <div className="text-muted-foreground">{icon}</div>
      <div className="text-sm font-medium text-foreground">{title}</div>
      <div className="text-xs text-muted-foreground">{description}</div>
    </div>
  );
}
