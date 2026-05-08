import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Save, Send, Archive } from "lucide-react";
import { toast } from "sonner";
import {
  kbApi,
  type KbArticleStatus,
  type KbSectionNode,
} from "@/lib/kb-api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useCurrentRole } from "@/hooks/useCurrentRole";
import { RichTextEditor } from "@/components/RichTextEditor";

type Props = {
  /// `null` puts the page in create-mode; an existing id loads + updates.
  articleId: string | null;
  /// Initial section selection when creating a new article. Ignored on edit.
  initialSectionId?: string;
};

/// Tiptap-backed composer for KB articles. Reuses the shared RichTextEditor
/// without the @@-mention or ::-intake popovers — neither callback is
/// passed, so those extensions stay dormant. Save flow is two-step:
/// upsert the article + translation, then optionally flip status. The
/// status-flip controls hide what the role can't perform; the API enforces
/// the same gate.
export function KbArticleEditPage({ articleId, initialSectionId }: Props) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const role = useCurrentRole();
  const isAdmin = role === "Admin";
  const isCreate = articleId === null;

  const { data: tree } = useQuery({
    queryKey: ["kb", "sections"],
    queryFn: kbApi.listSections,
  });

  const { data: existing, isLoading } = useQuery({
    queryKey: ["kb", "article", articleId, "body"],
    queryFn: () => kbApi.getArticle(articleId!, true),
    enabled: !isCreate,
  });

  const [sectionId, setSectionId] = useState<string>("");
  const [slug, setSlug] = useState("");
  const [title, setTitle] = useState("");
  const [bodyHtml, setBodyHtml] = useState("");
  const [editorNotes, setEditorNotes] = useState("");

  // Hydrate the form once data arrives. The dependency on `existing`
  // alone is intentional; setSectionId fires only when we transition from
  // null → loaded so user keystrokes during edit aren't clobbered.
  useEffect(() => {
    if (isCreate) {
      if (initialSectionId && !sectionId) setSectionId(initialSectionId);
      return;
    }
    if (existing?.article) {
      setSectionId(existing.article.sectionId);
      setSlug(existing.article.slug);
      setEditorNotes(existing.article.editorNotes ?? "");
    }
    if (existing?.translation) {
      setTitle(existing.translation.title);
      setBodyHtml(existing.translation.bodyHtml ?? "");
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [existing, isCreate, initialSectionId]);

  const sectionOptions = useMemo(
    () => flattenSections(tree?.tree ?? []),
    [tree],
  );

  const create = useMutation({
    mutationFn: () =>
      kbApi.createArticle({
        sectionId,
        slug: slug || undefined,
        title,
        bodyHtml,
        editorNotes: editorNotes || null,
      }),
    onSuccess: ({ article }) => {
      toast.success("Draft saved.");
      queryClient.invalidateQueries({ queryKey: ["kb"] });
      navigate({ to: "/kb/articles/$articleId/edit", params: { articleId: article.id } });
    },
    onError: () => toast.error("Could not create article."),
  });

  const update = useMutation({
    mutationFn: () =>
      kbApi.updateArticle(articleId!, {
        sectionId,
        slug: slug || undefined,
        title,
        bodyHtml,
        editorNotes: editorNotes || null,
      }),
    onSuccess: () => {
      toast.success("Article saved.");
      queryClient.invalidateQueries({ queryKey: ["kb"] });
    },
    onError: () => toast.error("Could not save article."),
  });

  const flip = useMutation({
    mutationFn: (target: KbArticleStatus) => kbApi.changeStatus(articleId!, target),
    onSuccess: (updated) => {
      toast.success(`Article is now ${updated.status}.`);
      queryClient.invalidateQueries({ queryKey: ["kb"] });
    },
    onError: () => toast.error("Could not change status."),
  });

  if (!isCreate && isLoading) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-8 w-96" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }

  const canSubmit = title.trim().length > 0 && sectionId.length > 0;
  const article = existing?.article;
  const status = article?.status ?? "Draft";

  return (
    <div className="flex min-h-[calc(100vh-8rem)] w-full flex-col gap-5">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="font-display text-display-md font-semibold text-foreground">
            {isCreate ? "New article" : "Edit article"}
          </h1>
          <p className="text-sm text-muted-foreground">
            Drafts are visible only to editors. Promote to Internal to share with
            agents, or Publish (admin) for the future customer portal.
          </p>
        </div>
      </header>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1fr)_320px]">
        <div className="flex flex-col gap-4">
          <Input
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Article title"
            className="h-12 text-lg"
          />

          <div className="glass-card overflow-hidden">
            <RichTextEditor
              content={bodyHtml}
              onChange={setBodyHtml}
              placeholder="Write the article body. Headings, lists, code, and links are supported."
              minHeight="320px"
              maxHeight="60vh"
            />
          </div>

          <div className="space-y-1">
            <label className="text-xs uppercase tracking-wider text-muted-foreground">
              Editor notes (private)
            </label>
            <textarea
              value={editorNotes}
              onChange={(e) => setEditorNotes(e.target.value)}
              placeholder="Internal context for editors. Never shown to customers."
              rows={3}
              className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm outline-none focus:border-white/20"
            />
          </div>
        </div>

        <aside className="flex flex-col gap-4">
          <div className="glass-card flex flex-col gap-3 p-5">
            <label className="space-y-1 text-xs">
              <span className="uppercase tracking-wider text-muted-foreground">Section</span>
              <select
                value={sectionId}
                onChange={(e) => setSectionId(e.target.value)}
                className="h-9 w-full rounded-md border border-white/10 bg-white/[0.03] px-2 text-sm text-foreground focus:border-white/20 focus:outline-none"
              >
                <option value="" disabled>Select a section…</option>
                {sectionOptions.map((opt) => (
                  <option key={opt.id} value={opt.id}>
                    {opt.indent}{opt.title}
                  </option>
                ))}
              </select>
            </label>
            <label className="space-y-1 text-xs">
              <span className="uppercase tracking-wider text-muted-foreground">Slug</span>
              <Input
                value={slug}
                onChange={(e) => setSlug(e.target.value.toLowerCase())}
                placeholder="auto-from-title-on-save"
                className="font-mono text-xs"
              />
              <span className="text-[10px] text-muted-foreground">
                Lowercase letters/digits separated by hyphens. Leave empty to keep the existing slug.
              </span>
            </label>

            <div className="border-t border-white/5 pt-3">
              <Button
                onClick={() => (isCreate ? create.mutate() : update.mutate())}
                disabled={!canSubmit || create.isPending || update.isPending}
                className="w-full gap-1"
              >
                <Save className="h-4 w-4" />
                {isCreate ? "Create draft" : "Save"}
              </Button>
            </div>

            {!isCreate && article && (
              <div className="grid grid-cols-2 gap-2">
                {(status === "Draft" || isAdmin) && (
                  <FlipButton
                    label="Promote to Internal"
                    icon={<Send className="h-3.5 w-3.5" />}
                    disabled={status === "Internal"}
                    onClick={() => flip.mutate("Internal")}
                  />
                )}
                {(status === "Internal" || isAdmin) && (
                  <FlipButton
                    label="Back to Draft"
                    icon={<Archive className="h-3.5 w-3.5" />}
                    disabled={status === "Draft"}
                    onClick={() => flip.mutate("Draft")}
                  />
                )}
                {isAdmin && (
                  <>
                    <FlipButton
                      label="Publish"
                      icon={<Send className="h-3.5 w-3.5" />}
                      disabled={status === "Published"}
                      onClick={() => flip.mutate("Published")}
                    />
                    <FlipButton
                      label="Archive"
                      icon={<Archive className="h-3.5 w-3.5" />}
                      disabled={status === "Archived"}
                      onClick={() => flip.mutate("Archived")}
                    />
                  </>
                )}
              </div>
            )}
          </div>
        </aside>
      </div>
    </div>
  );
}

function FlipButton({
  label, icon, disabled, onClick,
}: {
  label: string;
  icon: React.ReactNode;
  disabled: boolean;
  onClick: () => void;
}) {
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

function flattenSections(
  nodes: KbSectionNode[],
  depth = 0,
  out: { id: string; title: string; indent: string }[] = [],
): { id: string; title: string; indent: string }[] {
  for (const n of nodes) {
    out.push({ id: n.id, title: n.title, indent: depth === 0 ? "" : "—".repeat(depth) + " " });
    if (n.children.length > 0) flattenSections(n.children, depth + 1, out);
  }
  return out;
}
