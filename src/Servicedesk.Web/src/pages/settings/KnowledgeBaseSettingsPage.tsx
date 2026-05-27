import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2, FolderTree, Globe, Settings } from "lucide-react";
import { toast } from "sonner";
import {
  kbApi,
  type KbConfig,
  type KbLocale,
  type KbSectionNode,
} from "@/lib/kb-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

/// Admin-only Knowledge Base settings page. Three regions:
///   1. Config — active toggle + default locale.
///   2. Locales — add/remove supported locales (delete is gated server-side
///      to refuse a locale that still has translations).
///   3. Sections — flat overview of the section tree, with create/delete
///      and a parent-picker for nesting. Drag-and-drop reordering is a
///      v0.0.32+ refinement; v0.0.31 stays click-driven for predictability.
export function KnowledgeBaseSettingsPage() {
  const queryClient = useQueryClient();

  const { data: config, isLoading: configLoading } = useQuery({
    queryKey: ["kb", "config"],
    queryFn: kbApi.getConfig,
  });

  const { data: locales, isLoading: localesLoading } = useQuery({
    queryKey: ["kb", "locales", "all"],
    queryFn: () => kbApi.listLocales(true),
  });

  const { data: tree, isLoading: treeLoading } = useQuery({
    queryKey: ["kb", "sections"],
    queryFn: kbApi.listSections,
  });

  return (
    <div className="flex min-h-[calc(100vh-8rem)] w-full flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="font-display text-display-md font-semibold text-foreground">
            Knowledge Base
          </h1>
          <p className="text-sm text-muted-foreground">
            App-wide KB config, supported locales, and the section tree articles live in.
          </p>
        </div>
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      {configLoading || localesLoading ? (
        <Skeleton className="h-32 w-full" />
      ) : (
        config && locales && (
          <ConfigCard
            config={config}
            locales={locales}
            onSaved={() => queryClient.invalidateQueries({ queryKey: ["kb"] })}
          />
        )
      )}

      <LocalesCard
        locales={locales ?? []}
        defaultLocaleCode={config?.defaultLocaleCode ?? null}
        loading={localesLoading}
        onChanged={() => queryClient.invalidateQueries({ queryKey: ["kb"] })}
      />

      <SectionsCard
        tree={tree?.tree ?? []}
        loading={treeLoading}
        onChanged={() => queryClient.invalidateQueries({ queryKey: ["kb"] })}
      />
    </div>
  );
}

// ──────────────────────────────────────────────────────────────────────────
// Config
// ──────────────────────────────────────────────────────────────────────────

function ConfigCard({
  config, locales, onSaved,
}: {
  config: KbConfig;
  locales: KbLocale[];
  onSaved: () => void;
}) {
  const [isActive, setIsActive] = useState(config.isActive);
  const [defaultLocaleCode, setDefaultLocaleCode] = useState(config.defaultLocaleCode);

  useEffect(() => {
    setIsActive(config.isActive);
    setDefaultLocaleCode(config.defaultLocaleCode);
  }, [config]);

  const save = useMutation({
    mutationFn: () => kbApi.updateConfig({ isActive, defaultLocaleCode }),
    onSuccess: () => {
      toast.success("Config saved.");
      onSaved();
    },
    onError: () => toast.error("Could not save config."),
  });

  const dirty = isActive !== config.isActive || defaultLocaleCode !== config.defaultLocaleCode;

  return (
    <section className="glass-card flex flex-col gap-4 p-5">
      <div className="flex items-center gap-2 text-sm font-medium text-foreground">
        <Settings className="h-4 w-4 text-primary" /> Configuration
      </div>
      <div className="flex flex-wrap items-end gap-6">
        <label className="flex items-center gap-3 text-sm">
          <Switch checked={isActive} onCheckedChange={setIsActive} />
          <span>{isActive ? "KB is active" : "KB is paused"}</span>
        </label>
        <div className="flex flex-col gap-1 text-xs">
          <span className="uppercase tracking-wider text-muted-foreground">Default locale</span>
          <Select value={defaultLocaleCode} onValueChange={setDefaultLocaleCode}>
            <SelectTrigger className="h-9 min-w-[220px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {locales
                .filter((l) => l.isActive)
                .map((l) => (
                  <SelectItem key={l.code} value={l.code}>
                    {l.displayName} ({l.code})
                  </SelectItem>
                ))}
            </SelectContent>
          </Select>
        </div>
        <div className="ml-auto">
          <Button onClick={() => save.mutate()} disabled={!dirty || save.isPending}>
            Save
          </Button>
        </div>
      </div>
    </section>
  );
}

// ──────────────────────────────────────────────────────────────────────────
// Locales
// ──────────────────────────────────────────────────────────────────────────

function LocalesCard({
  locales, defaultLocaleCode, loading, onChanged,
}: {
  locales: KbLocale[];
  defaultLocaleCode: string | null;
  loading: boolean;
  onChanged: () => void;
}) {
  const [editing, setEditing] = useState<KbLocale | null | "new">(null);

  const remove = useMutation({
    mutationFn: (code: string) => kbApi.removeLocale(code),
    onSuccess: () => { toast.success("Locale removed."); onChanged(); },
    onError: (err: Error & { payload?: { error?: string } }) =>
      toast.error(err.payload?.error ?? "Could not remove locale."),
  });

  return (
    <section className="glass-card flex flex-col gap-4 p-5">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Globe className="h-4 w-4 text-primary" /> Locales
        </div>
        <Button size="sm" variant="outline" onClick={() => setEditing("new")} className="gap-1">
          <Plus className="h-3.5 w-3.5" /> Add locale
        </Button>
      </div>

      {loading ? (
        <Skeleton className="h-20" />
      ) : locales.length === 0 ? (
        <div className="rounded-md border border-dashed border-glass px-4 py-6 text-center text-sm text-muted-foreground">
          No locales — at least one must exist.
        </div>
      ) : (
        <table className="w-full text-left text-sm">
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-3 py-2 font-medium">Code</th>
              <th className="px-3 py-2 font-medium">Display name</th>
              <th className="px-3 py-2 font-medium">Active</th>
              <th className="px-3 py-2 font-medium">Default</th>
              <th className="px-3 py-2" />
            </tr>
          </thead>
          <tbody>
            {locales.map((l) => (
              <tr key={l.code} className="border-b border-glass last:border-b-0">
                <td className="px-3 py-2 font-mono text-xs">{l.code}</td>
                <td className="px-3 py-2 text-foreground">{l.displayName}</td>
                <td className="px-3 py-2 text-muted-foreground">{l.isActive ? "Yes" : "No"}</td>
                <td className="px-3 py-2 text-muted-foreground">
                  {l.code === defaultLocaleCode ? "★" : "—"}
                </td>
                <td className="px-3 py-2 text-right">
                  <button
                    type="button"
                    onClick={() => setEditing(l)}
                    className="mr-2 text-xs text-muted-foreground hover:text-foreground"
                  >
                    Edit
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      if (l.code === defaultLocaleCode) {
                        toast.error("Cannot remove the default locale. Change the default first.");
                        return;
                      }
                      if (!confirm(`Remove locale '${l.code}'? This is rejected if any translation still uses it.`))
                        return;
                      remove.mutate(l.code);
                    }}
                    className="text-xs text-destructive/80 hover:text-destructive"
                  >
                    Remove
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {editing && (
        <LocaleDialog
          initial={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); onChanged(); }}
        />
      )}
    </section>
  );
}

function LocaleDialog({
  initial, onClose, onSaved,
}: {
  initial: KbLocale | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [code, setCode] = useState(initial?.code ?? "");
  const [displayName, setDisplayName] = useState(initial?.displayName ?? "");
  const [isActive, setIsActive] = useState(initial?.isActive ?? true);
  const [sortOrder, setSortOrder] = useState(initial?.sortOrder ?? 0);
  const isNew = initial === null;

  const save = useMutation({
    mutationFn: () => kbApi.upsertLocale({ code, displayName, isActive, sortOrder }),
    onSuccess: () => { toast.success(isNew ? "Locale added." : "Locale updated."); onSaved(); },
    onError: (err: Error & { payload?: { error?: string } }) =>
      toast.error(err.payload?.error ?? "Could not save locale."),
  });

  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{isNew ? "Add locale" : `Edit locale ${initial!.code}`}</DialogTitle>
          <DialogDescription>
            BCP-47 code (e.g. nl-BE, en-US, fr-FR). Required by the per-locale translation tables.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <Input
            value={code}
            onChange={(e) => setCode(e.target.value)}
            placeholder="nl-BE"
            disabled={!isNew}
            className="font-mono"
          />
          <Input
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            placeholder="Nederlands (België)"
          />
          <label className="flex items-center gap-3 text-sm">
            <Switch checked={isActive} onCheckedChange={setIsActive} />
            <span>Active</span>
          </label>
          <label className="flex items-center gap-3 text-sm">
            <span className="text-xs uppercase tracking-wider text-muted-foreground">Order</span>
            <Input
              type="number"
              value={sortOrder}
              onChange={(e) => setSortOrder(Number(e.target.value))}
              className="w-24"
            />
          </label>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button
            onClick={() => save.mutate()}
            disabled={code.trim().length === 0 || displayName.trim().length === 0 || save.isPending}
          >
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ──────────────────────────────────────────────────────────────────────────
// Sections
// ──────────────────────────────────────────────────────────────────────────

function SectionsCard({
  tree, loading, onChanged,
}: {
  tree: KbSectionNode[];
  loading: boolean;
  onChanged: () => void;
}) {
  const [editing, setEditing] = useState<KbSectionNode | null | "new">(null);

  const flat = useMemo(() => flattenSections(tree), [tree]);

  const remove = useMutation({
    mutationFn: (id: string) => kbApi.deleteSection(id),
    onSuccess: () => { toast.success("Section removed."); onChanged(); },
    onError: (err: Error & { payload?: { error?: string } }) =>
      toast.error(err.payload?.error ?? "Could not remove section."),
  });

  return (
    <section className="glass-card flex flex-col gap-4 p-5">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <FolderTree className="h-4 w-4 text-primary" /> Sections
        </div>
        <Button size="sm" variant="outline" onClick={() => setEditing("new")} className="gap-1">
          <Plus className="h-3.5 w-3.5" /> New section
        </Button>
      </div>

      {loading ? (
        <Skeleton className="h-32" />
      ) : flat.length === 0 ? (
        <div className="rounded-md border border-dashed border-glass px-4 py-6 text-center text-sm text-muted-foreground">
          No sections yet.
        </div>
      ) : (
        <ul className="divide-y divide-glass">
          {flat.map((row) => (
            <li key={row.id} className="flex items-center justify-between gap-3 py-2">
              <div className="flex min-w-0 items-center gap-2">
                <span className="text-muted-foreground">{"—".repeat(row.depth)}</span>
                <span className="truncate text-sm text-foreground">{row.title}</span>
                <span className="font-mono text-[10px] text-muted-foreground">/{row.slug}</span>
              </div>
              <div className="flex items-center gap-2 text-xs">
                <button
                  type="button"
                  onClick={() => setEditing(row.node)}
                  className="text-muted-foreground hover:text-foreground"
                >
                  Edit
                </button>
                <button
                  type="button"
                  onClick={() => {
                    if (!confirm(`Remove '${row.title}'? Hard-delete only works on empty sections.`)) return;
                    remove.mutate(row.id);
                  }}
                  className="text-destructive/80 hover:text-destructive"
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}

      {editing && (
        <SectionDialog
          initial={editing === "new" ? null : editing}
          allSections={flat}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); onChanged(); }}
        />
      )}
    </section>
  );
}

function SectionDialog({
  initial, allSections, onClose, onSaved,
}: {
  initial: KbSectionNode | null;
  allSections: { id: string; title: string; depth: number; node: KbSectionNode }[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const isNew = initial === null;
  // The literal "" stands in for "no parent" because shadcn Select rejects
  // an empty-string value. We map "__root" ↔ null at the API boundary.
  const ROOT_SENTINEL = "__root";
  const [parentValue, setParentValue] = useState<string>(initial?.parentSectionId ?? ROOT_SENTINEL);
  const [title, setTitle] = useState(initial?.title ?? "");
  const [description, setDescription] = useState(initial?.description ?? "");

  const save = useMutation({
    mutationFn: () => {
      const body = {
        parentSectionId: parentValue === ROOT_SENTINEL ? null : parentValue,
        title,
        description: description || null,
      };
      return isNew
        ? kbApi.createSection(body).then(() => undefined)
        : kbApi.updateSection(initial!.id, body).then(() => undefined);
    },
    onSuccess: () => { toast.success(isNew ? "Section added." : "Section saved."); onSaved(); },
    onError: (err: Error & { payload?: { error?: string } }) =>
      toast.error(err.payload?.error ?? "Could not save section."),
  });

  // Self-parent and descendants are filtered out so the parent picker can't
  // create a cycle that the API would reject anyway.
  const parentOptions = useMemo(() => {
    if (isNew) return allSections;
    const blockedIds = collectIdsBelow(initial!);
    return allSections.filter((s) => !blockedIds.has(s.id));
  }, [allSections, initial, isNew]);

  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{isNew ? "New section" : `Edit ${initial!.title}`}</DialogTitle>
          <DialogDescription>
            Sections form a tree. Slugs are unique within siblings.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div className="space-y-1 text-xs">
            <span className="uppercase tracking-wider text-muted-foreground">Parent</span>
            <Select value={parentValue} onValueChange={setParentValue}>
              <SelectTrigger className="h-9">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ROOT_SENTINEL}>— Root —</SelectItem>
                {parentOptions.map((p) => (
                  <SelectItem key={p.id} value={p.id}>
                    {p.depth > 0 ? "—".repeat(p.depth) + " " : ""}{p.title}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <Input
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Section title"
          />
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Short description (optional, plain text)"
            rows={3}
            className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm outline-none focus:border-glass-strong"
          />
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button
            onClick={() => save.mutate()}
            disabled={title.trim().length === 0 || save.isPending}
          >
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function flattenSections(
  nodes: KbSectionNode[],
  depth = 0,
  out: { id: string; title: string; slug: string; depth: number; node: KbSectionNode }[] = [],
): { id: string; title: string; slug: string; depth: number; node: KbSectionNode }[] {
  for (const n of nodes) {
    out.push({ id: n.id, title: n.title, slug: n.slug, depth, node: n });
    if (n.children.length > 0) flattenSections(n.children, depth + 1, out);
  }
  return out;
}

function collectIdsBelow(node: KbSectionNode, out: Set<string> = new Set()): Set<string> {
  out.add(node.id);
  for (const c of node.children) collectIdsBelow(c, out);
  return out;
}
