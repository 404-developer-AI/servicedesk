import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ClipboardList, History, Loader2, Minus, Pencil, Trash2 } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { useAuth } from "@/auth/authStore";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import {
  formApiError,
  ticketFormKeys,
  ticketFormsApi,
  type FormAnswerValue,
  type FormAnswers,
  type FormField,
  type RichTextAnswer,
  type TicketFormDetail,
} from "@/lib/forms-api";
import { SafeHtml } from "@/components/SafeHtml";
import { formDraftKey, useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { draftFromAnswers, draftToInput, emptyDraft, validateDraft, type FormDraft } from "@/lib/formDraft";
import { cn } from "@/lib/utils";
import { composeTemplatesApi } from "@/lib/composeTemplates-api";
import { FormFieldInput, formInputCn, type FieldPicker } from "./FormFieldInput";
import { formFieldPickerItems } from "./formFieldPicker";

/// v0.1.52 — dialogs for ticket forms. Fill = a fresh form from a template
/// (picked via `::`), Edit = a new version of a saved form, View = the latest
/// answers + version history. Saving always writes an internal note on the
/// server; the dialogs only render and collect.

/* ─── shared form body ─── */

function FormBody({
  fields,
  draft,
  setDraft,
  errors,
  ticketId,
  disabled,
  picker,
}: {
  fields: FormField[];
  draft: FormDraft;
  setDraft: React.Dispatch<React.SetStateAction<FormDraft>>;
  errors: Record<string, string>;
  ticketId: string | null;
  disabled?: boolean;
  picker?: FieldPicker;
}) {
  return (
    <div className="sd-form-body grid grid-cols-2 gap-x-4 gap-y-4">
      {fields.map((f) => (
        <FormFieldInput
          key={f.id}
          field={f}
          value={draft[f.id]}
          ticketId={ticketId}
          error={errors[f.id]}
          disabled={disabled}
          picker={picker}
          onChange={(v) => setDraft((d) => ({ ...d, [f.id]: v }))}
        />
      ))}
    </div>
  );
}

export function FormPreview({ fields }: { fields: FormField[] }) {
  const [draft, setDraft] = React.useState<FormDraft>(() => emptyDraft(fields));
  React.useEffect(() => {
    // Keep entered preview values for fields that still exist; add new ones.
    setDraft((d) => ({ ...emptyDraft(fields), ...Object.fromEntries(Object.entries(d).filter(([k]) => fields.some((f) => f.id === k))) }));
  }, [fields]);
  return <FormBody fields={fields} draft={draft} setDraft={setDraft} errors={{}} ticketId={null} />;
}

/* ─── fill / edit ─── */

type FillTarget =
  | { mode: "create"; ticketId: string; templateId: string }
  | { mode: "edit"; ticketId: string; form: TicketFormDetail };

export function FormFillDialog({
  target,
  ticketNumber,
  onOpenChange,
  onSaved,
}: {
  target: FillTarget | null;
  /** Shown on the minimised tray chip. */
  ticketNumber?: number | null;
  onOpenChange: (open: boolean) => void;
  onSaved?: (form: TicketFormDetail) => void;
}) {
  const open = target !== null;
  const qc = useQueryClient();
  const template = useQuery({
    queryKey: ["forms", "fill-template", target?.mode === "create" ? target.ticketId : null, target?.mode === "create" ? target.templateId : null],
    queryFn: () => ticketFormsApi.template(target!.ticketId, (target as { templateId: string }).templateId),
    enabled: target?.mode === "create",
    staleTime: 0,
    retry: false,
  });

  const fields: FormField[] | null =
    target?.mode === "edit" ? target.form.fields : template.data?.fields ?? null;
  const title = target?.mode === "edit" ? target.form.name : template.data?.name ?? "Form";
  const description = target?.mode === "edit"
    ? `Editing saves version ${target.form.version + 1} and adds an internal note with the changes.`
    : template.data?.description || "Saving adds the filled form to the ticket as an internal note.";

  // v0.1.55 — `::` in text fields: this ticket's variables, orders, KB.
  // Same query key as the composer, so an open composer shares the result.
  const { user } = useAuth();
  const tokens = useQuery({
    queryKey: ["compose-templates", "resolve", { ticketId: target?.ticketId }],
    queryFn: () => composeTemplatesApi.resolveTokens({ ticketId: target!.ticketId }),
    enabled: open,
    staleTime: 60_000,
  });
  const tokenMap = tokens.data?.tokens;
  const ordersEnabled = !!user?.adsolutOrdersEnabled;
  const kbEnabled = !!user?.kbEnabled;
  const picker = React.useCallback<FieldPicker>(
    (q) => formFieldPickerItems({ tokens: tokenMap, ordersEnabled, kbEnabled }, q),
    [tokenMap, ordersEnabled, kbEnabled],
  );

  // v0.1.55 — the answers live in the workspace store as soon as they
  // differ from where the dialog started, so they survive minimising,
  // switching tickets and a reload (see FormDraftEntry).
  const draftKey = target
    ? formDraftKey(target.mode === "create"
      ? { mode: "create", ticketId: target.ticketId, templateId: target.templateId }
      : { mode: "edit", ticketId: target.ticketId, formId: target.form.id })
    : null;
  const setFormDraft = useWorkspaceStore((s) => s.setFormDraft);
  const setFormDraftMinimized = useWorkspaceStore((s) => s.setFormDraftMinimized);
  const removeFormDraft = useWorkspaceStore((s) => s.removeFormDraft);
  const hasStoredDraft = useWorkspaceStore((s) => (draftKey ? !!s.formDrafts[draftKey] : false));

  const [draft, setDraft] = React.useState<FormDraft>({});
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [confirmDiscard, setConfirmDiscard] = React.useState(false);
  const [initialised, setInitialised] = React.useState<string | null>(null);
  const baseline = React.useRef<string>("");
  // The version an edit started from — a restored draft keeps its own, so a
  // form changed by someone else in the meantime still 409s on save.
  const baseVersion = React.useRef<number>(0);

  // (Re)initialise when a new target / template arrives. A fresh form waits
  // for the ticket's variables so `{{…}}` defaults are filled in (a failed
  // lookup fills them in empty rather than blocking the form). A stored
  // draft for the same ticket + template / form is resumed.
  const tokensSettled = tokens.isSuccess || tokens.isError;
  const ready = fields !== null && (target?.mode === "edit" || tokensSettled);
  const initKey = target?.mode === "edit" ? `e:${target.form.id}:${target.form.version}` : target ? `c:${target.templateId}:${template.dataUpdatedAt}` : "";
  React.useEffect(() => {
    if (!fields || !ready || !target || !draftKey) return;
    const fresh = target.mode === "edit" ? draftFromAnswers(fields, target.form.answers) : emptyDraft(fields, tokenMap ?? {});
    const stored = useWorkspaceStore.getState().formDrafts[draftKey];
    const ids = new Set(fields.map((f) => f.id));
    const d = stored
      ? { ...fresh, ...Object.fromEntries(Object.entries(stored.draft).filter(([k]) => ids.has(k))) }
      : fresh;
    setDraft(d);
    setErrors({});
    setConfirmDiscard(false);
    baseline.current = stored?.baseline ?? JSON.stringify(draftToInput(fields, fresh));
    baseVersion.current = target.mode === "edit" ? stored?.version ?? target.form.version : 0;
    if (stored) setFormDraftMinimized(draftKey, false);
    setInitialised(draftKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initKey, ready]);

  const live = initialised !== null && initialised === draftKey;
  const dirty = fields !== null && live && JSON.stringify(draftToInput(fields, draft)) !== baseline.current;

  // Mirror a changed draft into the store (flushed to the server by the
  // workspace auto-save on navigation / tab hide / unload). Back to the
  // starting answers = nothing to keep.
  React.useEffect(() => {
    if (!live || !target || !draftKey || !fields) return;
    if (!dirty) {
      removeFormDraft(draftKey);
      return;
    }
    setFormDraft({
      key: draftKey,
      ticketId: target.ticketId,
      ticketNumber: ticketNumber ?? null,
      formName: title,
      mode: target.mode,
      templateId: target.mode === "create" ? target.templateId : undefined,
      formId: target.mode === "edit" ? target.form.id : undefined,
      version: target.mode === "edit" ? baseVersion.current : undefined,
      draft,
      baseline: baseline.current,
      minimized: false,
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft, live]);

  // Leaving while the dialog is open (ticket switch, navigation, unmount)
  // never loses the answers: a stored draft drops into the tray.
  React.useEffect(() => {
    if (!draftKey) return;
    return () => setFormDraftMinimized(draftKey, true);
  }, [draftKey, setFormDraftMinimized]);

  const save = useMutation({
    mutationFn: async () => {
      if (!target || !fields) throw new Error("No form");
      const answers = draftToInput(fields, draft);
      return target.mode === "create"
        ? ticketFormsApi.create(target.ticketId, target.templateId, answers)
        : ticketFormsApi.update(target.form.id, baseVersion.current, answers);
    },
    onSuccess: (saved) => {
      qc.setQueryData(ticketFormKeys.detail(saved.ticketId, saved.id), saved);
      void qc.invalidateQueries({ queryKey: ["ticket", saved.ticketId] });
      toast.success(target?.mode === "edit" ? `Form updated (v${saved.version})` : "Form saved as internal note");
      if (draftKey) removeFormDraft(draftKey);
      setInitialised(null);
      onSaved?.(saved);
      onOpenChange(false);
    },
    onError: (err) => {
      const e = formApiError(err);
      if (e?.fieldId) setErrors((prev) => ({ ...prev, [e.fieldId!]: e.error ?? "Invalid." }));
      toast.error(e?.error ?? "Could not save the form.");
    },
  });

  const submit = () => {
    if (!fields) return;
    const errs = validateDraft(fields, draft);
    setErrors(errs);
    if (Object.keys(errs).length > 0) {
      requestAnimationFrame(() => document.getElementById(`ff-${Object.keys(errs)[0]}`)?.focus());
      return;
    }
    save.mutate();
  };

  const minimize = () => {
    if (save.isPending) return;
    if (draftKey) setFormDraftMinimized(draftKey, true);
    setInitialised(null);
    onOpenChange(false);
  };

  const discard = () => {
    if (draftKey) removeFormDraft(draftKey);
    setInitialised(null);
    onOpenChange(false);
  };

  // Esc, a click outside and the ✕ keep a changed form (minimised);
  // only Cancel → Discard throws the answers away.
  const onDialogOpenChange = (next: boolean) => {
    if (next || save.isPending) return;
    if (dirty) minimize();
    else discard();
  };

  const cancel = () => {
    if (save.isPending) return;
    if (dirty) setConfirmDiscard(true);
    else discard();
  };

  const notAvailable = target?.mode === "create" && template.isError;

  return (
    <Dialog open={open} onOpenChange={onDialogOpenChange}>
      <DialogContent
        className="sd-form-dialog flex max-h-[90vh] w-[95vw] max-w-3xl flex-col gap-0 p-0"
        onKeyDown={(e) => {
          if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            submit();
          }
        }}
      >
        <button type="button" onClick={minimize} disabled={save.isPending}
          title="Minimise: keep the answers and continue elsewhere"
          className="absolute right-11 top-4 rounded-sm opacity-70 ring-offset-background transition-opacity hover:opacity-100 focus:outline-hidden focus:ring-2 focus:ring-ring focus:ring-offset-2 disabled:pointer-events-none">
          <Minus className="h-4 w-4" />
          <span className="sr-only">Minimise</span>
        </button>
        <DialogHeader className="border-b border-glass px-6 pb-4 pt-5">
          <div className="flex items-start gap-3 pr-14">
            <span className="sd-form-icon mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border border-primary/25 bg-primary/10 text-primary">
              <ClipboardList className="h-4 w-4" />
            </span>
            <div className="min-w-0">
              <DialogTitle className="truncate text-lg">{title}</DialogTitle>
              <DialogDescription className="mt-0.5">{description}</DialogDescription>
            </div>
          </div>
        </DialogHeader>

        <div className="min-h-0 flex-1 overflow-y-auto px-6 py-5">
          {notAvailable ? (
            <div className="space-y-3 py-6">
              <p className="text-sm text-rose-600 dark:text-rose-400">
                {formApiError(template.error)?.error ?? "This form is not available for this ticket."}
              </p>
              {hasStoredDraft && (
                <Button variant="outline" size="sm" onClick={discard}>Discard draft</Button>
              )}
            </div>
          ) : !fields || !live ? (
            <div className="space-y-4">
              <Skeleton className="h-9 w-full" />
              <Skeleton className="h-9 w-2/3" />
              <Skeleton className="h-24 w-full" />
            </div>
          ) : (
            <FormBody fields={fields} draft={draft} setDraft={setDraft} errors={errors}
              ticketId={target?.ticketId ?? null} disabled={save.isPending} picker={picker} />
          )}
        </div>

        <div className="flex items-center justify-between gap-3 border-t border-glass px-6 py-3.5">
          {confirmDiscard ? (
            <>
              <span className="text-sm text-amber-700 dark:text-amber-300">Discard the answers you entered?</span>
              <div className="flex gap-2">
                <Button variant="ghost" size="sm" onClick={() => setConfirmDiscard(false)}>Keep editing</Button>
                <Button variant="destructive" size="sm" onClick={discard}>Discard</Button>
              </div>
            </>
          ) : (
            <>
              <span className="hidden text-[11px] text-muted-foreground/60 sm:block">
                Internal only · :: in a text field inserts a variable, order or article · Ctrl+Enter to save
              </span>
              <div className="ml-auto flex gap-2">
                <Button variant="ghost" size="sm" onClick={cancel} disabled={save.isPending}>
                  Cancel
                </Button>
                <Button size="sm" onClick={submit} disabled={!fields || !live || save.isPending || notAvailable || (target?.mode === "edit" && !dirty)}>
                  {save.isPending && <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />}
                  {target?.mode === "edit" ? "Save new version" : "Save as internal note"}
                </Button>
              </div>
            </>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}

/* ─── read view ─── */

function displayValue(f: FormField, v: FormAnswerValue | undefined, zone: string | null): string {
  if (v === undefined || v === null) return "";
  switch (f.type) {
    case "Checkbox":
      return v === true ? "Yes" : "No";
    case "Dropdown":
      return f.options?.find((o) => o.id === v)?.label ?? String(v);
    case "MultiSelect":
    case "CheckboxGroup":
      return (Array.isArray(v) ? (v as string[]) : []).map((id) => f.options?.find((o) => o.id === id)?.label ?? id).join(", ");
    case "Date": {
      const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(v));
      return m ? `${m[3]}/${m[2]}/${m[1]}` : String(v);
    }
    case "DateTime":
      return formatDateTime(String(v), zone);
    case "Duration": {
      const n = Number(v);
      return `${Math.floor(n / 60)}:${String(n % 60).padStart(2, "0")}`;
    }
    case "Agent":
    case "Contact": {
      const r = v as { name?: string; email?: string };
      return r.name && r.email ? `${r.name} · ${r.email}` : r.name || r.email || "";
    }
    default:
      return String(v);
  }
}

export function FormAnswersView({ fields, answers }: { fields: FormField[]; answers: FormAnswers }) {
  const zone = useServerTimeZone();
  return (
    <dl className="sd-form-answers grid grid-cols-2 gap-x-6 gap-y-4">
      {fields.map((f) => {
        if (f.type === "SectionHeader") {
          return (
            <div key={f.id} className="col-span-2 flex items-center gap-3 pt-2 first:pt-0">
              <h3 className="text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">{f.label}</h3>
              <div className="h-px flex-1 bg-border/60" />
            </div>
          );
        }
        const v = answers[f.id];
        const span = f.width === "half" ? "col-span-2 sm:col-span-1" : "col-span-2";
        if (f.type === "Article") {
          const rows = Array.isArray(v) ? (v as { articleId: string; code?: string; name?: string; quantity: number }[]) : [];
          return (
            <div key={f.id} className={span}>
              <dt className="text-xs font-medium text-muted-foreground">{f.label}</dt>
              <dd className="mt-1">
                {rows.length === 0 ? <span className="text-sm text-muted-foreground/50">—</span> : (
                  <table className="w-full overflow-hidden rounded-md border border-glass text-sm">
                    <tbody className="divide-y divide-border/50">
                      {rows.map((r) => (
                        <tr key={r.articleId}>
                          <td className="w-16 px-3 py-1.5 text-right font-medium tabular-nums">{r.quantity}×</td>
                          <td className="px-2 py-1.5 font-mono text-[11px] text-muted-foreground">{r.code}</td>
                          <td className="px-3 py-1.5">{r.name}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </dd>
            </div>
          );
        }
        if (f.type === "LongText" && v && typeof v === "object" && "html" in v) {
          return (
            <div key={f.id} className={span}>
              <dt className="text-xs font-medium text-muted-foreground">{f.label}</dt>
              <dd className="mt-0.5 break-words text-sm">
                <SafeHtml html={(v as RichTextAnswer).html} />
              </dd>
            </div>
          );
        }
        const text = displayValue(f, v, zone);
        return (
          <div key={f.id} className={span}>
            <dt className="text-xs font-medium text-muted-foreground">{f.label}</dt>
            <dd className={cn("mt-0.5 whitespace-pre-wrap break-words text-sm", !text && "text-muted-foreground/50")}>
              {text || "—"}
            </dd>
          </div>
        );
      })}
    </dl>
  );
}

export function FormViewDialog({
  ticketId,
  formId,
  onOpenChange,
  onEdit,
}: {
  ticketId: string;
  formId: string | null;
  onOpenChange: (open: boolean) => void;
  onEdit: (form: TicketFormDetail) => void;
}) {
  const open = formId !== null;
  const qc = useQueryClient();
  const zone = useServerTimeZone();
  const { user } = useAuth();
  const isAdmin = user?.role === "Admin";
  const [showHistory, setShowHistory] = React.useState(false);
  const [viewVersion, setViewVersion] = React.useState<number | null>(null);
  const [removing, setRemoving] = React.useState(false);
  const [reason, setReason] = React.useState("");

  React.useEffect(() => {
    setShowHistory(false);
    setViewVersion(null);
    setRemoving(false);
    setReason("");
  }, [formId]);

  const form = useQuery({
    queryKey: ticketFormKeys.detail(ticketId, formId ?? ""),
    queryFn: () => ticketFormsApi.get(formId!),
    enabled: open,
    retry: false,
  });
  const versions = useQuery({
    queryKey: ticketFormKeys.versions(ticketId, formId ?? ""),
    queryFn: () => ticketFormsApi.versions(formId!),
    enabled: open && showHistory,
  });

  const remove = useMutation({
    mutationFn: () => ticketFormsApi.remove(formId!, reason.trim()),
    onSuccess: () => {
      toast.success("Form removed");
      void qc.invalidateQueries({ queryKey: ["ticket", ticketId] });
      onOpenChange(false);
    },
    onError: (err) => toast.error(formApiError(err)?.error ?? "Could not remove the form."),
  });

  const data = form.data;
  const shownAnswers = viewVersion !== null
    ? versions.data?.find((v) => v.version === viewVersion)?.answers ?? data?.answers
    : data?.answers;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sd-form-dialog flex max-h-[90vh] w-[95vw] max-w-3xl flex-col gap-0 p-0">
        <DialogHeader className="border-b border-glass px-6 pb-4 pt-5">
          <div className="flex items-start gap-3">
            <span className="sd-form-icon mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border border-primary/25 bg-primary/10 text-primary">
              <ClipboardList className="h-4 w-4" />
            </span>
            <div className="min-w-0 flex-1">
              <DialogTitle className="flex items-center gap-2 text-lg">
                <span className="truncate">{data?.name ?? "Form"}</span>
                {data && (
                  <span className="shrink-0 rounded-full border border-glass-strong bg-glass px-2 py-0.5 text-[10px] font-semibold tabular-nums text-muted-foreground">
                    v{viewVersion ?? data.version}
                  </span>
                )}
              </DialogTitle>
              <DialogDescription className="mt-0.5">
                {data
                  ? data.version > 1
                    ? `Last updated by ${data.updatedByName ?? "unknown"} · ${formatDateTime(data.updatedUtc, zone)}`
                    : `Filled in by ${data.createdByName ?? "unknown"} · ${formatDateTime(data.createdUtc, zone)}`
                  : "Loading…"}
              </DialogDescription>
            </div>
          </div>
        </DialogHeader>

        <div className="flex min-h-0 flex-1">
          <div className="min-h-0 flex-1 overflow-y-auto px-6 py-5">
            {form.isLoading ? (
              <div className="space-y-4">
                <Skeleton className="h-10 w-full" />
                <Skeleton className="h-10 w-2/3" />
              </div>
            ) : form.isError || !data ? (
              <p className="py-6 text-sm text-rose-600 dark:text-rose-400">This form could not be loaded. It may have been removed.</p>
            ) : (
              <>
                {viewVersion !== null && viewVersion !== data.version && (
                  <div className="mb-4 flex items-center justify-between rounded-md border border-amber-400/40 bg-amber-100/60 px-3 py-2 text-xs text-amber-900 dark:bg-amber-400/10 dark:text-amber-200">
                    <span>You are looking at version {viewVersion}.</span>
                    <button type="button" className="font-medium underline-offset-2 hover:underline" onClick={() => setViewVersion(null)}>
                      Show latest
                    </button>
                  </div>
                )}
                <FormAnswersView fields={data.fields} answers={shownAnswers ?? {}} />
              </>
            )}
          </div>
          {showHistory && data && (
            <aside className="sd-form-history w-56 shrink-0 overflow-y-auto border-l border-glass px-3 py-4">
              <div className="mb-2 px-1 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground/70">History</div>
              {versions.isLoading ? <Skeleton className="h-16 w-full" /> : (
                <ol className="space-y-1">
                  {(versions.data ?? []).map((v) => {
                    const active = (viewVersion ?? data.version) === v.version;
                    return (
                      <li key={v.version}>
                        <button type="button" onClick={() => setViewVersion(v.version === data.version ? null : v.version)}
                          className={cn("w-full rounded-md px-2 py-1.5 text-left transition-colors",
                            active ? "bg-primary/10 text-foreground" : "text-muted-foreground hover:bg-glass-hover")}>
                          <span className="block text-xs font-semibold tabular-nums">v{v.version}</span>
                          <span className="block truncate text-[11px]">{v.savedByName ?? "unknown"}</span>
                          <span className="block text-[10px] tabular-nums opacity-70">{formatDateTime(v.savedUtc, zone)}</span>
                        </button>
                      </li>
                    );
                  })}
                </ol>
              )}
            </aside>
          )}
        </div>

        <div className="flex flex-wrap items-center gap-2 border-t border-glass px-6 py-3.5">
          {removing ? (
            <>
              <input autoFocus value={reason} maxLength={1000} onChange={(e) => setReason(e.target.value)}
                placeholder="Reason for removing (required)" className={cn(formInputCn, "h-8 min-w-0 flex-1")} />
              <Button variant="ghost" size="sm" onClick={() => setRemoving(false)}>Cancel</Button>
              <Button variant="destructive" size="sm" disabled={!reason.trim() || remove.isPending} onClick={() => remove.mutate()}>
                Remove form
              </Button>
            </>
          ) : (
            <>
              <Button variant="ghost" size="sm" onClick={() => setShowHistory((s) => !s)} disabled={!data || data.version < 2}
                title={data && data.version < 2 ? "No earlier versions" : undefined}>
                <History className="mr-1.5 h-3.5 w-3.5" />
                {showHistory ? "Hide history" : "History"}
              </Button>
              {isAdmin && data && (
                <Button variant="ghost" size="sm" className="text-rose-600 hover:text-rose-600 dark:text-rose-400" onClick={() => setRemoving(true)}>
                  <Trash2 className="mr-1.5 h-3.5 w-3.5" />
                  Remove
                </Button>
              )}
              <div className="ml-auto flex gap-2">
                <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)}>Close</Button>
                <Button size="sm" disabled={!data} onClick={() => data && onEdit(data)}>
                  <Pencil className="mr-1.5 h-3.5 w-3.5" />
                  Edit
                </Button>
              </div>
            </>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
