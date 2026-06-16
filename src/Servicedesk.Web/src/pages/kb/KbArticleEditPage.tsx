import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Save, Send, Archive, ImageOff, Paperclip, Trash2, Download } from "lucide-react";
import { toast } from "sonner";
import {
  kbApi,
  type KbArticleStatus,
  type KbAttachment,
  type KbSectionNode,
} from "@/lib/kb-api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
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

  const attachmentsQuery = useQuery({
    queryKey: ["kb", "article", articleId, "attachments"],
    queryFn: () => kbApi.listAttachments(articleId!),
    enabled: !isCreate,
  });
  // The panel lists file attachments only — inline images live in the body.
  const fileAttachments = useMemo(
    () => (attachmentsQuery.data?.items ?? []).filter((a) => !a.mimeType.startsWith("image/")),
    [attachmentsQuery.data],
  );
  const deleteAttachment = useMutation({
    mutationFn: (attachmentId: string) => kbApi.deleteAttachment(articleId!, attachmentId),
    onSuccess: () => {
      toast.success("Attachment removed.");
      queryClient.invalidateQueries({ queryKey: ["kb", "article", articleId, "attachments"] });
    },
    onError: () => toast.error("Could not remove attachment."),
  });

  const [sectionId, setSectionId] = useState<string>("");
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
        title,
        bodyHtml,
        editorNotes: editorNotes || null,
      }),
    onSuccess: () => {
      toast.success("Article saved.");
      queryClient.invalidateQueries({ queryKey: ["kb"] });
      // Saving an edit means "I'm done writing" — drop back into the
      // reader so the result is immediately visible. Create stays in edit
      // mode on purpose (image uploads need a persisted article first).
      navigate({ to: "/kb/articles/$articleId", params: { articleId: articleId! } });
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

          {isCreate && (
            <div className="flex items-start gap-2 rounded-lg border border-amber-300/30 bg-amber-300/[0.06] px-3 py-2 text-xs text-amber-200">
              <ImageOff className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <div>
                <span className="font-medium">Image uploads are unavailable until the draft is saved.</span>{" "}
                Click <span className="font-medium">Create draft</span> on the right to enable paste, drag-and-drop,
                and the paperclip button.
              </div>
            </div>
          )}
          <div className="glass-card overflow-hidden">
            <RichTextEditor
              content={bodyHtml}
              onChange={setBodyHtml}
              placeholder="Write the article body. Headings, lists, code, links, and images are supported."
              minHeight="320px"
              maxHeight="60vh"
              linkNonImageUploads
              onUploadFile={async (file) => {
                if (isCreate) {
                  toast.warning(
                    "Save the draft first — images are attached to an existing article.",
                    { duration: 6000 },
                  );
                  return null;
                }
                try {
                  const meta = await kbApi.uploadAttachment(articleId!, file);
                  // Refresh the Attachments panel so a freshly added file shows
                  // up there too (not only as a body link / inline image).
                  queryClient.invalidateQueries({
                    queryKey: ["kb", "article", articleId, "attachments"],
                  });
                  return {
                    id: meta.id,
                    url: meta.url,
                    mimeType: meta.mimeType,
                    size: meta.size,
                    filename: meta.filename,
                  };
                } catch (err) {
                  const e = err as Error & { payload?: { error?: string } };
                  toast.error(e.payload?.error ?? "Upload failed.");
                  return null;
                }
              }}
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
              className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm outline-none focus:border-glass-strong"
            />
          </div>
        </div>

        <aside className="flex flex-col gap-4">
          <div className="glass-card flex flex-col gap-3 p-5">
            <div className="space-y-1 text-xs">
              <span className="uppercase tracking-wider text-muted-foreground">Section</span>
              <Select value={sectionId} onValueChange={setSectionId}>
                <SelectTrigger className="h-9">
                  <SelectValue placeholder="Select a section…" />
                </SelectTrigger>
                <SelectContent>
                  {sectionOptions.map((opt) => (
                    <SelectItem key={opt.id} value={opt.id}>
                      {opt.indent}{opt.title}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            <div className="border-t border-glass pt-3">
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

          {!isCreate && (
            <div className="glass-card flex flex-col gap-3 p-5">
              <div className="flex items-center gap-2 text-xs uppercase tracking-wider text-muted-foreground">
                <Paperclip className="h-3.5 w-3.5" />
                Attachments
                {fileAttachments.length > 0 && (
                  <span className="tabular-nums text-muted-foreground/60">
                    ({fileAttachments.length})
                  </span>
                )}
              </div>

              {fileAttachments.length === 0 ? (
                <p className="text-xs text-muted-foreground/70">
                  No file attachments yet. Drag a file into the editor, paste it,
                  or use the paperclip — non-image files are listed here.
                </p>
              ) : (
                <ul className="flex flex-col gap-1.5">
                  {fileAttachments.map((a) => (
                    <AttachmentRow
                      key={a.id}
                      attachment={a}
                      onDelete={() => {
                        if (confirm(`Remove "${a.filename}" from this article?`)) {
                          deleteAttachment.mutate(a.id);
                        }
                      }}
                      deleting={deleteAttachment.isPending}
                    />
                  ))}
                </ul>
              )}
            </div>
          )}
        </aside>
      </div>
    </div>
  );
}

function AttachmentRow({
  attachment,
  onDelete,
  deleting,
}: {
  attachment: KbAttachment;
  onDelete: () => void;
  deleting: boolean;
}) {
  return (
    <li className="flex items-center gap-2 rounded-md border border-glass bg-glass px-2.5 py-1.5">
      <a
        href={attachment.url}
        target="_blank"
        rel="noreferrer"
        className="flex min-w-0 flex-1 items-center gap-2 text-xs text-foreground hover:text-primary"
        title={`Download ${attachment.filename}`}
      >
        <Download className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
        <span className="min-w-0 flex-1 truncate">{attachment.filename}</span>
        <span className="shrink-0 tabular-nums text-muted-foreground/60">
          {formatBytes(attachment.size)}
        </span>
      </a>
      <button
        type="button"
        onClick={onDelete}
        disabled={deleting}
        title="Remove attachment"
        aria-label={`Remove ${attachment.filename}`}
        className="shrink-0 rounded p-1 text-muted-foreground/60 transition-colors hover:bg-glass-hover hover:text-rose-300 disabled:opacity-40"
      >
        <Trash2 className="h-3.5 w-3.5" />
      </button>
    </li>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
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
      className="flex items-center justify-center gap-1.5 rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
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
