import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Check, ChevronDown, Minus, Package, Plus, Search, Trash2, UserCircle, Users } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { DateTimePicker } from "@/components/ui/datetime-picker";
import { useServerOffsetMinutes, useServerTimeZone } from "@/hooks/useServerTime";
import { userApi } from "@/lib/ticket-api";
import { ticketFormsApi, type FormField } from "@/lib/forms-api";
import { newRowKey, type ArticleDraftRow, type DraftValue } from "@/lib/formDraft";
import { cn } from "@/lib/utils";

/// v0.1.52 — one input per form field type. Shared by the fill dialog and the
/// admin editor's live preview (pass `ticketId = null` there: lookups that
/// need a ticket render disabled with a hint).

export const formInputCn =
  "sd-form-input w-full rounded-md border border-glass-strong bg-glass px-3 text-sm text-foreground outline-hidden transition-colors placeholder:text-muted-foreground/50 focus:border-primary/50 focus:bg-glass-strong disabled:opacity-60";

type Props = {
  field: FormField;
  value: DraftValue | undefined;
  onChange: (v: DraftValue) => void;
  ticketId: string | null;
  error?: string;
  disabled?: boolean;
};

export function FormFieldInput({ field, value, onChange, ticketId, error, disabled }: Props) {
  if (field.type === "SectionHeader") {
    return (
      <div className="sd-form-section col-span-2 pt-3 first:pt-0">
        <div className="flex items-center gap-3">
          <h3 className="text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">
            {field.label}
          </h3>
          <div className="h-px flex-1 bg-border/60" />
        </div>
        {field.help && <p className="mt-1 text-xs text-muted-foreground/70">{field.help}</p>}
      </div>
    );
  }

  const id = `ff-${field.id}`;
  const control = renderControl(field, value, onChange, ticketId, id, !!error, disabled);
  const inlineLabel = field.type === "Checkbox";

  return (
    <div className={cn("sd-form-field flex min-w-0 flex-col gap-1.5", field.width === "half" ? "col-span-2 sm:col-span-1" : "col-span-2")}>
      {!inlineLabel && (
        <label htmlFor={id} className="flex items-baseline gap-1 text-[13px] font-medium text-foreground/90">
          <span className="min-w-0">{field.label}</span>
          {field.required && <span className="text-rose-500" aria-label="required">*</span>}
        </label>
      )}
      {control}
      {field.help && !inlineLabel && <p className="text-xs leading-snug text-muted-foreground/70">{field.help}</p>}
      {error && <p className="text-xs font-medium text-rose-600 dark:text-rose-400">{error}</p>}
    </div>
  );
}

function renderControl(
  f: FormField,
  value: DraftValue | undefined,
  onChange: (v: DraftValue) => void,
  ticketId: string | null,
  id: string,
  invalid: boolean,
  disabled?: boolean,
): React.ReactNode {
  const ring = invalid ? "border-rose-500/60 focus:border-rose-500/80" : "";
  const text = value?.kind === "text" ? value.value : "";
  switch (f.type) {
    case "ShortText":
      return (
        <input id={id} type="text" maxLength={500} disabled={disabled} value={text}
          onChange={(e) => onChange({ kind: "text", value: e.target.value })}
          className={cn(formInputCn, "h-9", ring)} />
      );
    case "LongText":
      return (
        <textarea id={id} rows={4} disabled={disabled} value={text}
          onChange={(e) => onChange({ kind: "text", value: e.target.value })}
          className={cn(formInputCn, "min-h-[96px] resize-y py-2 leading-relaxed", ring)} />
      );
    case "Number":
      return (
        <input id={id} type="text" inputMode="decimal" disabled={disabled} value={text}
          placeholder={f.min != null || f.max != null ? `${f.min ?? "…"} – ${f.max ?? "…"}` : undefined}
          onChange={(e) => onChange({ kind: "text", value: e.target.value })}
          className={cn(formInputCn, "h-9 tabular-nums", ring)} />
      );
    case "Duration":
      return (
        <input id={id} type="text" inputMode="numeric" disabled={disabled} value={text} placeholder="h:mm"
          onChange={(e) => onChange({ kind: "text", value: e.target.value })}
          className={cn(formInputCn, "h-9 w-32 tabular-nums", ring)} />
      );
    case "Date":
      return (
        <input id={id} type="date" disabled={disabled} value={text}
          onChange={(e) => onChange({ kind: "text", value: e.target.value })}
          className={cn(formInputCn, "h-9 w-48", ring)} />
      );
    case "DateTime":
      return <DateTimeControl value={value?.kind === "datetime" ? value.value : null} disabled={disabled}
        onChange={(v) => onChange({ kind: "datetime", value: v })} />;
    case "Checkbox": {
      const checked = value?.kind === "bool" && value.value;
      return (
        <label htmlFor={id} className={cn(
          "sd-form-check flex cursor-pointer items-start gap-3 rounded-md border px-3 py-2.5 transition-colors",
          checked ? "border-primary/40 bg-primary/8" : "border-glass-strong bg-glass hover:bg-glass-hover",
          invalid && "border-rose-500/60",
          disabled && "pointer-events-none opacity-60",
        )}>
          <CheckBox id={id} checked={checked} onChange={(v) => onChange({ kind: "bool", value: v })} />
          <span className="min-w-0 text-[13px]">
            <span className="font-medium text-foreground/90">{f.label}</span>
            {f.required && <span className="ml-1 text-rose-500">*</span>}
            {f.help && <span className="mt-0.5 block text-xs text-muted-foreground/70">{f.help}</span>}
          </span>
        </label>
      );
    }
    case "Dropdown": {
      const current = text.length > 0 ? text : "__none";
      return (
        <Select disabled={disabled} value={current} onValueChange={(v) => onChange({ kind: "text", value: v === "__none" ? "" : v })}>
          <SelectTrigger id={id} className={cn("h-9 border-glass-strong bg-glass text-sm focus:ring-0", ring)}>
            <SelectValue placeholder="Choose…" />
          </SelectTrigger>
          <SelectContent className="border-glass bg-popover/90 backdrop-blur-xl">
            <SelectItem value="__none"><span className="text-muted-foreground">—</span></SelectItem>
            {(f.options ?? []).map((o) => <SelectItem key={o.id} value={o.id}>{o.label}</SelectItem>)}
          </SelectContent>
        </Select>
      );
    }
    case "MultiSelect": {
      const selected = value?.kind === "options" ? value.value : [];
      return (
        <div className="flex flex-wrap gap-1.5" role="group" aria-labelledby={id}>
          {(f.options ?? []).map((o) => {
            const on = selected.includes(o.id);
            return (
              <button key={o.id} type="button" disabled={disabled} aria-pressed={on}
                onClick={() => onChange({ kind: "options", value: on ? selected.filter((x) => x !== o.id) : [...selected, o.id] })}
                className={cn(
                  "sd-form-chip inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs font-medium transition-colors",
                  on ? "border-primary/50 bg-primary/12 text-foreground" : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
                )}>
                {on && <Check className="h-3 w-3" />}
                {o.label}
              </button>
            );
          })}
        </div>
      );
    }
    case "CheckboxGroup": {
      const selected = value?.kind === "options" ? value.value : [];
      return (
        <div className={cn("sd-form-checklist grid gap-1 rounded-md border border-glass-strong bg-glass p-1.5", invalid && "border-rose-500/60")}>
          {(f.options ?? []).map((o) => {
            const on = selected.includes(o.id);
            return (
              <label key={o.id} className={cn("flex cursor-pointer items-center gap-2.5 rounded px-2 py-1.5 text-[13px] transition-colors hover:bg-glass-hover", disabled && "pointer-events-none opacity-60")}>
                <CheckBox checked={on} onChange={(v) => onChange({ kind: "options", value: v ? [...selected, o.id] : selected.filter((x) => x !== o.id) })} />
                <span className={on ? "text-foreground" : "text-foreground/80"}>{o.label}</span>
              </label>
            );
          })}
        </div>
      );
    }
    case "Agent":
      return <AgentControl value={value?.kind === "ref" ? value.value : null} disabled={disabled} invalid={invalid}
        onChange={(v) => onChange({ kind: "ref", value: v })} />;
    case "Contact":
      return <ContactControl ticketId={ticketId} value={value?.kind === "ref" ? value.value : null} disabled={disabled}
        invalid={invalid} onChange={(v) => onChange({ kind: "ref", value: v })} />;
    case "Article":
      return <ArticleRowsControl field={f} ticketId={ticketId} rows={value?.kind === "articles" ? value.value : []}
        disabled={disabled} invalid={invalid} onChange={(rows) => onChange({ kind: "articles", value: rows })} />;
  }
  return null;
}

/* ─── primitives ─── */

function CheckBox({ id, checked, onChange }: { id?: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <span className="relative mt-px inline-flex h-4 w-4 shrink-0">
      <input id={id} type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)}
        className="peer absolute inset-0 cursor-pointer opacity-0" />
      <span className={cn(
        "sd-form-checkbox pointer-events-none flex h-4 w-4 items-center justify-center rounded-[4px] border transition-colors peer-focus-visible:ring-2 peer-focus-visible:ring-primary/40",
        checked ? "border-primary bg-primary text-primary-foreground" : "border-foreground/30 bg-background/60",
      )}>
        {checked && <Check className="h-3 w-3" strokeWidth={3} />}
      </span>
    </span>
  );
}

function DateTimeControl({ value, onChange, disabled }: { value: string | null; onChange: (v: string | null) => void; disabled?: boolean }) {
  const offset = useServerOffsetMinutes();
  const zone = useServerTimeZone();
  return (
    <DateTimePicker value={value} offsetMinutes={offset ?? 0} timeZone={zone} onChange={onChange}
      disabled={disabled} placeholder="Pick date and time" className="w-64" />
  );
}

/// Popover + search box + list. `options` is called with the debounced term.
function SearchPicker<T extends { id: string }>({
  icon: Icon,
  label,
  placeholder,
  disabled,
  invalid,
  hint,
  load,
  queryKey,
  renderItem,
  onPick,
  onClear,
  extra,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: React.ReactNode | null;
  placeholder: string;
  disabled?: boolean;
  invalid?: boolean;
  hint?: string;
  load: (term: string) => Promise<T[]>;
  queryKey: readonly unknown[];
  renderItem: (item: T) => React.ReactNode;
  onPick: (item: T) => void;
  onClear?: () => void;
  extra?: React.ReactNode;
}) {
  const [open, setOpen] = React.useState(false);
  const [term, setTerm] = React.useState("");
  const [debounced, setDebounced] = React.useState("");
  React.useEffect(() => {
    const t = setTimeout(() => setDebounced(term.trim()), 250);
    return () => clearTimeout(t);
  }, [term]);
  const q = useQuery({
    queryKey: [...queryKey, debounced],
    queryFn: () => load(debounced),
    enabled: open && !disabled,
    staleTime: 30_000,
  });
  return (
    <Popover open={open} onOpenChange={(o) => { setOpen(o); if (!o) setTerm(""); }}>
      <PopoverTrigger asChild>
        <button type="button" disabled={disabled}
          className={cn(formInputCn, "flex h-9 items-center justify-between gap-2 text-left", !label && "text-muted-foreground/60", invalid && "border-rose-500/60")}>
          <span className="flex min-w-0 items-center gap-2">
            <Icon className="h-3.5 w-3.5 shrink-0 opacity-50" />
            <span className="truncate">{label ?? placeholder}</span>
          </span>
          <ChevronDown className="h-3.5 w-3.5 shrink-0 opacity-40" />
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" className="glass-card w-[min(380px,90vw)] border-glass p-0">
        <div className="flex items-center gap-2 border-b border-glass px-3">
          <Search className="h-3.5 w-3.5 shrink-0 text-muted-foreground/60" />
          <input autoFocus value={term} onChange={(e) => setTerm(e.target.value)} placeholder="Search…"
            className="h-10 w-full bg-transparent text-sm outline-hidden placeholder:text-muted-foreground/50" />
        </div>
        {extra}
        <div className="max-h-[280px] overflow-y-auto p-1">
          {onClear && label && (
            <button type="button" onClick={() => { onClear(); setOpen(false); }}
              className="w-full rounded px-3 py-1.5 text-left text-xs text-muted-foreground hover:bg-glass-hover">
              Clear selection
            </button>
          )}
          {q.isLoading ? (
            <div className="px-3 py-3 text-xs text-muted-foreground/70">Searching…</div>
          ) : (q.data ?? []).length === 0 ? (
            <div className="px-3 py-3 text-xs text-muted-foreground/70">{hint ?? "No matches"}</div>
          ) : (
            (q.data ?? []).map((item) => (
              <button key={item.id} type="button" onClick={() => { onPick(item); setOpen(false); setTerm(""); }}
                className="w-full rounded px-3 py-2 text-left text-sm transition-colors hover:bg-glass-hover">
                {renderItem(item)}
              </button>
            ))
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}

function AgentControl({ value, onChange, disabled, invalid }: {
  value: { id: string; name: string } | null; onChange: (v: { id: string; name: string } | null) => void;
  disabled?: boolean; invalid?: boolean;
}) {
  const agents = useQuery({ queryKey: ["agents"], queryFn: userApi.listAgents, staleTime: 5 * 60_000 });
  return (
    <SearchPicker
      icon={UserCircle}
      label={value?.name ?? null}
      placeholder="Choose an agent"
      disabled={disabled}
      invalid={invalid}
      queryKey={["forms", "agent-options", agents.dataUpdatedAt]}
      load={async (term) => {
        const list = agents.data ?? (await userApi.listAgents());
        const needle = term.toLowerCase();
        return list.filter((a) => !needle || a.email.toLowerCase().includes(needle)).slice(0, 50);
      }}
      renderItem={(a) => <span className="truncate">{a.email}</span>}
      onPick={(a) => onChange({ id: a.id, name: a.email })}
      onClear={() => onChange(null)}
    />
  );
}

function ContactControl({ ticketId, value, onChange, disabled, invalid }: {
  ticketId: string | null; value: { id: string; name: string; email?: string } | null;
  onChange: (v: { id: string; name: string; email?: string } | null) => void; disabled?: boolean; invalid?: boolean;
}) {
  return (
    <SearchPicker
      icon={Users}
      label={value ? (value.name || value.email || "Contact") : null}
      placeholder={ticketId ? "Choose a contact of this company" : "Contacts of the ticket's company"}
      disabled={disabled || !ticketId}
      invalid={invalid}
      hint="No contacts — only the requester and contacts of the ticket's company can be chosen."
      queryKey={["forms", "contact-options", ticketId]}
      load={(term) => ticketFormsApi.contactOptions(ticketId!, term)}
      renderItem={(c) => (
        <span className="block min-w-0">
          <span className="block truncate font-medium">{c.name || c.email}</span>
          {c.name && <span className="block truncate text-xs text-muted-foreground">{c.email}</span>}
        </span>
      )}
      onPick={(c) => onChange({ id: c.id, name: c.name, email: c.email })}
      onClear={() => onChange(null)}
    />
  );
}

function ArticleRowsControl({ field, ticketId, rows, onChange, disabled, invalid }: {
  field: FormField; ticketId: string | null; rows: ArticleDraftRow[];
  onChange: (rows: ArticleDraftRow[]) => void; disabled?: boolean; invalid?: boolean;
}) {
  const [scope, setScope] = React.useState<"contract" | "all">("contract");
  const full = field.maxRows != null && rows.length >= field.maxRows;
  const step = (q: string, delta: number) => {
    const n = Number(q.replace(",", "."));
    const base = Number.isFinite(n) ? n : 0;
    const next = Math.max(field.min ?? 1, base + delta);
    return String(field.max != null ? Math.min(field.max, next) : next);
  };

  return (
    <div className={cn("sd-form-articles overflow-hidden rounded-md border border-glass-strong bg-glass", invalid && "border-rose-500/60")}>
      {rows.length > 0 && (
        <ul className="divide-y divide-border/50">
          {rows.map((r) => (
            <li key={r.key} className="flex items-center gap-3 px-3 py-2">
              <Package className="h-3.5 w-3.5 shrink-0 text-muted-foreground/60" />
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[13px] font-medium">{r.name || r.code}</span>
                {r.code && <span className="block truncate font-mono text-[11px] text-muted-foreground">{r.code}</span>}
              </span>
              <div className="flex items-center rounded-md border border-glass-strong bg-background/40">
                <button type="button" disabled={disabled} aria-label="Decrease"
                  onClick={() => onChange(rows.map((x) => x.key === r.key ? { ...x, quantity: step(x.quantity, -1) } : x))}
                  className="flex h-7 w-7 items-center justify-center text-muted-foreground hover:text-foreground">
                  <Minus className="h-3 w-3" />
                </button>
                <input value={r.quantity} disabled={disabled} inputMode="decimal" aria-label={`Quantity ${r.code}`}
                  onChange={(e) => onChange(rows.map((x) => x.key === r.key ? { ...x, quantity: e.target.value } : x))}
                  className="h-7 w-14 bg-transparent text-center text-sm tabular-nums outline-hidden" />
                <button type="button" disabled={disabled} aria-label="Increase"
                  onClick={() => onChange(rows.map((x) => x.key === r.key ? { ...x, quantity: step(x.quantity, 1) } : x))}
                  className="flex h-7 w-7 items-center justify-center text-muted-foreground hover:text-foreground">
                  <Plus className="h-3 w-3" />
                </button>
              </div>
              <button type="button" disabled={disabled} aria-label="Remove article"
                onClick={() => onChange(rows.filter((x) => x.key !== r.key))}
                className="rounded p-1 text-muted-foreground/60 transition-colors hover:bg-rose-500/10 hover:text-rose-500">
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            </li>
          ))}
        </ul>
      )}
      <div className={cn("p-2", rows.length > 0 && "border-t border-border/50")}>
        <SearchPicker
          icon={Plus}
          label={null}
          placeholder={full ? `Maximum of ${field.maxRows} articles reached` : "Add an article"}
          disabled={disabled || !ticketId || full}
          queryKey={["forms", "article-options", ticketId, scope]}
          hint={scope === "contract"
            ? "No contract articles for this company. Switch to “All articles” to search the full catalogue."
            : "No matching articles."}
          load={(term) => ticketFormsApi.articleOptions(ticketId!, term, scope)}
          extra={
            <div className="flex gap-1 border-b border-glass p-1.5">
              {(["contract", "all"] as const).map((s) => (
                <button key={s} type="button" onClick={() => setScope(s)}
                  className={cn("flex-1 rounded px-2 py-1 text-xs font-medium transition-colors",
                    scope === s ? "bg-primary/12 text-foreground" : "text-muted-foreground hover:bg-glass-hover")}>
                  {s === "contract" ? "Contract articles" : "All articles"}
                </button>
              ))}
            </div>
          }
          renderItem={(a) => (
            <span className="flex min-w-0 items-baseline gap-2">
              <span className="shrink-0 font-mono text-[11px] text-muted-foreground">{a.code}</span>
              <span className="truncate">{a.name}</span>
              {rows.some((x) => x.articleId === a.id) && <Check className="ml-auto h-3.5 w-3.5 shrink-0 text-primary" />}
            </span>
          )}
          onPick={(a) => {
            const existing = rows.find((x) => x.articleId === a.id);
            if (existing) {
              onChange(rows.map((x) => x.articleId === a.id ? { ...x, quantity: step(x.quantity, 1) } : x));
              return;
            }
            onChange([...rows, { key: newRowKey(), articleId: a.id, code: a.code, name: a.name, quantity: String(Math.max(1, field.min ?? 1)) }]);
          }}
        />
        {!ticketId && <p className="mt-1.5 px-1 text-[11px] text-muted-foreground/60">Articles are searched on the ticket.</p>}
      </div>
    </div>
  );
}
