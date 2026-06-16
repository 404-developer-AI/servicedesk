import { useEffect, useRef, useState } from "react";
import { AlertTriangle, Loader2, Plus, Send, Star, X } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  contractReportsApi,
  type ReportPreview,
  type ReportRecipient,
  type ReportTemplate,
} from "@/lib/contractReports-api";
import { ApiError } from "@/lib/api";

type Props = {
  companyId: string;
  companyName: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSent?: () => void;
};

export function SendReportModal({
  companyId,
  companyName,
  open,
  onOpenChange,
  onSent,
}: Props) {
  const qc = useQueryClient();

  const [templateId, setTemplateId] = useState<string | null>(null);
  const [columns, setColumns] = useState<string[]>([]);
  const [scope, setScope] = useState<"all" | "unprotected">("all");
  const [recipients, setRecipients] = useState<ReportRecipient[]>([]);
  const [freeEmail, setFreeEmail] = useState("");
  const [preview, setPreview] = useState<ReportPreview | null>(null);

  const lastColumnsRef = useRef<string[]>([]);
  // Pre-fill recipients from reporting contacts exactly once per open. Without
  // this guard, the contacts refetch triggered by starring a contact would
  // reset the list and wipe any free-form / manually toggled recipients.
  const recipientsInitedRef = useRef(false);

  const templatesQ = useQuery({
    queryKey: ["contract-report-templates", "active"],
    queryFn: () => contractReportsApi.templates.list(false),
    enabled: open,
  });
  const activeTemplates: ReportTemplate[] = templatesQ.data ?? [];

  const columnsQ = useQuery({
    queryKey: ["contract-report-columns"],
    queryFn: () => contractReportsApi.columns(),
    staleTime: Infinity,
    enabled: open,
  });
  const availableColumns = columnsQ.data?.columns ?? [];

  const contactsQ = useQuery({
    queryKey: ["contract-report-reporting-contacts", companyId],
    queryFn: () => contractReportsApi.reportingContacts(companyId),
    enabled: open,
  });
  const allContacts = contactsQ.data?.items ?? [];

  const selectedTemplate = activeTemplates.find((t) => t.id === templateId) ?? null;

  useEffect(() => {
    if (!selectedTemplate) return;
    const saved = lastColumnsRef.current;
    if (saved.length > 0) {
      setColumns(saved);
    } else {
      setColumns(selectedTemplate.columns);
    }
    setScope(selectedTemplate.scope);
  }, [selectedTemplate]);

  useEffect(() => {
    if (recipientsInitedRef.current) return;
    if (allContacts.length === 0) return;
    const reporting = allContacts.filter((c) => c.isReportingContact);
    setRecipients(
      reporting.map((c) => ({
        address: c.email,
        name: `${c.firstName} ${c.lastName}`.trim(),
      })),
    );
    recipientsInitedRef.current = true;
  }, [allContacts]);

  // Reset when dialog closes
  useEffect(() => {
    if (!open) {
      setTemplateId(null);
      setColumns([]);
      setScope("all");
      setRecipients([]);
      setFreeEmail("");
      setPreview(null);
      recipientsInitedRef.current = false;
    }
  }, [open]);

  const setReportingContact = useMutation({
    mutationFn: ({
      contactId,
      isReporting,
    }: {
      contactId: string;
      isReporting: boolean;
    }) => contractReportsApi.setReportingContact(companyId, contactId, isReporting),
    onSuccess: () => {
      qc.invalidateQueries({
        queryKey: ["contract-report-reporting-contacts", companyId],
      });
    },
    onError: () => toast.error("Could not update reporting contact."),
  });

  const previewMutation = useMutation({
    mutationFn: () => {
      if (!templateId) throw new Error("No template selected");
      return contractReportsApi.preview(companyId, {
        templateId,
        columns,
        scope,
        recipients,
      });
    },
    onSuccess: (data) => setPreview(data),
    onError: (err) => {
      const e = err as ApiError;
      const msg =
        (e.body as { error?: string } | null)?.error ??
        "Preview failed. The template may be missing.";
      toast.error(msg);
    },
  });

  const sendMutation = useMutation({
    mutationFn: () => {
      if (!templateId) throw new Error("No template selected");
      return contractReportsApi.send(companyId, {
        templateId,
        columns,
        scope,
        recipients,
      });
    },
    onSuccess: () => {
      toast.success("Report sent.");
      onSent?.();
      onOpenChange(false);
    },
    onError: (err) => {
      const e = err as ApiError;
      const msg =
        (e.body as { error?: string } | null)?.error ?? "Could not send report.";
      toast.error(msg);
    },
  });

  const toggleColumn = (key: string) => {
    setColumns((prev) => {
      const next = prev.includes(key) ? prev.filter((c) => c !== key) : [...prev, key];
      lastColumnsRef.current = next;
      return next;
    });
    setPreview(null);
  };

  const addFreeEmail = () => {
    const trimmed = freeEmail.trim();
    if (!trimmed) return;
    if (recipients.some((r) => r.address.toLowerCase() === trimmed.toLowerCase())) {
      setFreeEmail("");
      return;
    }
    setRecipients((prev) => [...prev, { address: trimmed, name: trimmed }]);
    setFreeEmail("");
    setPreview(null);
  };

  const removeRecipient = (address: string) => {
    setRecipients((prev) => prev.filter((r) => r.address !== address));
    setPreview(null);
  };

  const hasReportingContacts = allContacts.some((c) => c.isReportingContact);
  const canSend = templateId !== null && recipients.length > 0 && !sendMutation.isPending;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="flex max-h-[90vh] w-full max-w-2xl flex-col overflow-hidden border border-glass bg-background/95 backdrop-blur-xl">
        <DialogHeader>
          <DialogTitle className="text-base font-semibold text-foreground">
            Send report — {companyName}
          </DialogTitle>
        </DialogHeader>

        <div className="flex-1 overflow-y-auto">
          <div className="flex flex-col gap-5 pb-4">
            {/* Template select */}
            <div className="space-y-1.5">
              <label className="text-xs font-medium text-muted-foreground">Template</label>
              {templatesQ.isLoading ? (
                <p className="text-xs text-muted-foreground">Loading templates…</p>
              ) : (
                <Select
                  value={templateId ?? "__none"}
                  onValueChange={(v) => {
                    setTemplateId(v === "__none" ? null : v);
                    setPreview(null);
                  }}
                >
                  <SelectTrigger className="h-9 bg-glass">
                    <SelectValue placeholder="Choose a template…" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__none">Choose a template…</SelectItem>
                    {activeTemplates.map((t) => (
                      <SelectItem key={t.id} value={t.id}>
                        {t.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
            </div>

            {templateId && (
              <>
                {/* Scope */}
                <div className="space-y-1.5">
                  <label className="text-xs font-medium text-muted-foreground">Scope</label>
                  <div className="flex w-fit items-center gap-1 rounded-lg border border-glass-strong bg-glass p-1">
                    <ScopeButton active={scope === "all"} onClick={() => { setScope("all"); setPreview(null); }}>
                      All mailboxes
                    </ScopeButton>
                    <ScopeButton active={scope === "unprotected"} onClick={() => { setScope("unprotected"); setPreview(null); }}>
                      Unprotected only
                    </ScopeButton>
                  </div>
                </div>

                {/* Columns */}
                <div className="space-y-1.5">
                  <label className="text-xs font-medium text-muted-foreground">Columns</label>
                  {columnsQ.isLoading ? (
                    <p className="text-xs text-muted-foreground">Loading columns…</p>
                  ) : (
                    <div className="flex flex-wrap gap-2">
                      {availableColumns.map((col) => {
                        const selected = columns.includes(col.key);
                        return (
                          <button
                            key={col.key}
                            type="button"
                            onClick={() => toggleColumn(col.key)}
                            className={cn(
                              "inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs transition-colors",
                              selected
                                ? "border-primary/40 bg-primary/15 text-foreground"
                                : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                            )}
                            aria-pressed={selected}
                          >
                            {col.label}
                          </button>
                        );
                      })}
                    </div>
                  )}
                </div>

                {/* Recipients */}
                <div className="space-y-2">
                  <label className="text-xs font-medium text-muted-foreground">Recipients</label>

                  {!hasReportingContacts && (
                    <div className="flex items-start gap-2 rounded-md border border-amber-400/20 bg-amber-500/[0.06] p-3 text-xs text-amber-200">
                      <AlertTriangle className="mt-0.5 h-4 w-4 flex-none" />
                      <span>
                        No reporting contacts set for this company. Star a contact below to designate them, or add a free-form address.
                      </span>
                    </div>
                  )}

                  {/* Linked contacts with star toggle */}
                  {allContacts.length > 0 && (
                    <div className="rounded-lg border border-glass bg-glass">
                      {allContacts.map((c) => {
                        const isChecked = recipients.some(
                          (r) => r.address.toLowerCase() === c.email.toLowerCase(),
                        );
                        return (
                          <label
                            key={c.contactId}
                            className="flex cursor-pointer items-center gap-3 px-3 py-2 text-sm hover:bg-glass-hover"
                          >
                            <input
                              type="checkbox"
                              checked={isChecked}
                              onChange={(e) => {
                                if (e.target.checked) {
                                  setRecipients((prev) => [
                                    ...prev,
                                    {
                                      address: c.email,
                                      name: `${c.firstName} ${c.lastName}`.trim(),
                                    },
                                  ]);
                                } else {
                                  removeRecipient(c.email);
                                }
                                setPreview(null);
                              }}
                              className="rounded border-glass-strong bg-glass accent-primary"
                            />
                            <span className="flex-1 truncate text-foreground">
                              {c.firstName} {c.lastName}
                              <span className="ml-2 text-xs text-muted-foreground">{c.email}</span>
                            </span>
                            <button
                              type="button"
                              title={
                                c.isReportingContact
                                  ? "Remove as reporting contact"
                                  : "Mark as reporting contact"
                              }
                              className={cn(
                                "flex h-6 w-6 items-center justify-center rounded transition-colors hover:text-foreground",
                                c.isReportingContact
                                  ? "text-amber-400"
                                  : "text-muted-foreground/40",
                              )}
                              onClick={(e) => {
                                e.preventDefault();
                                setReportingContact.mutate({
                                  contactId: c.contactId,
                                  isReporting: !c.isReportingContact,
                                });
                              }}
                            >
                              <Star
                                className="h-3.5 w-3.5"
                                fill={c.isReportingContact ? "currentColor" : "none"}
                              />
                            </button>
                          </label>
                        );
                      })}
                    </div>
                  )}

                  {/* Selected recipients as chips */}
                  {recipients.length > 0 && (
                    <div className="flex flex-wrap gap-1.5">
                      {recipients.map((r) => (
                        <span
                          key={r.address}
                          className="inline-flex items-center gap-1.5 rounded-full border border-glass bg-glass px-2.5 py-1 text-xs text-foreground"
                        >
                          {r.name !== r.address ? (
                            <>
                              <span>{r.name}</span>
                              <span className="text-muted-foreground">&lt;{r.address}&gt;</span>
                            </>
                          ) : (
                            <span>{r.address}</span>
                          )}
                          <button
                            type="button"
                            onClick={() => { removeRecipient(r.address); }}
                            className="ml-0.5 text-muted-foreground hover:text-foreground"
                          >
                            <X className="h-3 w-3" />
                          </button>
                        </span>
                      ))}
                    </div>
                  )}

                  {/* Free-form add */}
                  <div className="flex gap-2">
                    <Input
                      type="email"
                      value={freeEmail}
                      onChange={(e) => setFreeEmail(e.target.value)}
                      onKeyDown={(e) => {
                        if (e.key === "Enter") { e.preventDefault(); addFreeEmail(); }
                      }}
                      placeholder="Add recipient by email address…"
                      className="h-8 text-sm"
                    />
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={addFreeEmail}
                      disabled={!freeEmail.trim()}
                    >
                      <Plus className="h-3.5 w-3.5" />
                      Add
                    </Button>
                  </div>
                </div>

                {/* Preview result */}
                {preview && (
                  <div className="space-y-3">
                    {preview.warnings.length > 0 && (
                      <div className="rounded-lg border border-amber-400/20 bg-amber-500/[0.06] p-3">
                        <div className="mb-1.5 flex items-center gap-1.5 text-xs font-medium text-amber-200">
                          <AlertTriangle className="h-3.5 w-3.5" />
                          Warnings
                        </div>
                        <ul className="space-y-0.5 text-xs text-amber-200/80">
                          {preview.warnings.map((w, i) => (
                            <li key={i}>{w}</li>
                          ))}
                        </ul>
                      </div>
                    )}

                    <div className="rounded-lg border border-glass bg-glass p-3 text-xs">
                      <p className="mb-1 font-medium text-foreground">Subject</p>
                      <p className="text-muted-foreground">{preview.subject}</p>
                    </div>

                    <div className="rounded-lg border border-glass bg-glass p-3 text-xs">
                      <p className="mb-1 font-medium text-foreground">Recipients</p>
                      <div className="flex flex-wrap gap-1.5">
                        {preview.recipients.map((r) => (
                          <span
                            key={r.address}
                            className="rounded-full border border-glass-strong bg-glass px-2 py-0.5 text-muted-foreground"
                          >
                            {r.name !== r.address ? `${r.name} <${r.address}>` : r.address}
                          </span>
                        ))}
                      </div>
                    </div>

                    <div className="grid grid-cols-2 gap-2 text-xs sm:grid-cols-3">
                      <StatPill label="Mailboxes" value={String(preview.mailboxCount)} />
                      {preview.spamTotal !== null && (
                        <StatPill
                          label="Spam filter"
                          value={`${preview.spamProtected ?? 0} / ${preview.spamTotal}`}
                        />
                      )}
                      {preview.exchangeTotal !== null && (
                        <StatPill
                          label="Exchange backup"
                          value={`${preview.exchangeProtected ?? 0} / ${preview.exchangeTotal}`}
                        />
                      )}
                      {preview.onedriveTotal !== null && (
                        <StatPill
                          label="OneDrive backup"
                          value={`${preview.onedriveProtected ?? 0} / ${preview.onedriveTotal}`}
                        />
                      )}
                      <StatPill label="Attach PDF" value={preview.attachPdf ? "Yes" : "No"} />
                      <StatPill
                        label="Scope"
                        value={preview.scope === "unprotected" ? "Unprotected only" : "All mailboxes"}
                      />
                    </div>

                    {/* Email body preview on white background (email rendering context) */}
                    <div className="space-y-1">
                      <p className="text-xs font-medium text-muted-foreground">Email preview</p>
                      <div className="max-h-72 overflow-y-auto rounded-lg border border-glass">
                        <div
                          className="bg-white p-4 text-sm text-gray-900"
                          // Server-sanitized HTML rendered as an email preview
                          // eslint-disable-next-line react/no-danger
                          dangerouslySetInnerHTML={{ __html: preview.bodyHtml }}
                        />
                      </div>
                    </div>
                  </div>
                )}
              </>
            )}
          </div>
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between gap-3 border-t border-glass pt-4">
          <Button
            variant="outline"
            size="sm"
            onClick={() => previewMutation.mutate()}
            disabled={!templateId || previewMutation.isPending}
            className="gap-1.5"
          >
            {previewMutation.isPending ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
            ) : null}
            Preview
          </Button>
          <div className="flex gap-2">
            <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button
              size="sm"
              className="gap-1.5"
              disabled={!canSend}
              onClick={() => sendMutation.mutate()}
            >
              {sendMutation.isPending ? (
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
              ) : (
                <Send className="h-3.5 w-3.5" />
              )}
              Send report
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function ScopeButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "rounded-md px-3 py-1.5 text-sm font-medium transition-colors",
        active
          ? "bg-primary/15 text-foreground"
          : "text-muted-foreground hover:text-foreground",
      )}
    >
      {children}
    </button>
  );
}

function StatPill({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-glass bg-glass px-3 py-2 text-xs">
      <p className="text-muted-foreground">{label}</p>
      <p className="mt-0.5 font-medium text-foreground">{value}</p>
    </div>
  );
}
