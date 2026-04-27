import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import { Save, X, History } from "lucide-react";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
} from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { Skeleton } from "@/components/ui/skeleton";
import { taxonomyApi, triggerApi, type TriggerDetail, type TriggerInput, type TriggerMetadata } from "@/lib/api";
import { ConditionsEditor } from "./ConditionsEditor";
import { ActionsEditor } from "./ActionsEditor";
import { TestRunner } from "./TestRunner";
import {
  blankActionForKind,
  isExpertConditions,
  parseActions,
  parseConditions,
  serializeActions,
  type ConditionGroup,
  type TriggerAction,
} from "./types";
import { cn } from "@/lib/utils";

type Props = {
  triggerId: string | "new" | null;
  metadata: TriggerMetadata;
  onClose: () => void;
};

/// Right-hand sheet that hosts the full Header / Activator / Conditions /
/// Actions form. Loads the current row when editing, validates locally
/// before posting, then submits the JSON-encoded conditions + actions to
/// the admin endpoint. Unsaved-changes guard via state-mirroring: the form
/// dirty-checks against the snapshot the data hydrated with.
export function TriggerEditorSheet({ triggerId, metadata, onClose }: Props) {
  const open = triggerId !== null;
  return (
    <Sheet open={open} onOpenChange={(o) => { if (!o) onClose(); }}>
      <SheetContent
        side="right"
        className="!w-[min(720px,95vw)] !max-w-none overflow-y-auto bg-[hsl(240_10%_5%/0.96)] backdrop-blur-xl border-l border-white/10 sm:!max-w-none"
      >
        <SheetHeader className="space-y-1">
          <SheetTitle>{triggerId === "new" ? "New trigger" : "Edit trigger"}</SheetTitle>
          <SheetDescription className="text-xs">
            Define when the trigger fires, which tickets it matches, and the
            actions it applies. Mail templates support <code className="font-mono">#{"{ticket.subject}"}</code>-style variables.
          </SheetDescription>
        </SheetHeader>
        {triggerId === null ? null : (
          <EditorBody triggerId={triggerId} metadata={metadata} onClose={onClose} />
        )}
      </SheetContent>
    </Sheet>
  );
}

function EditorBody({
  triggerId,
  metadata,
  onClose,
}: {
  triggerId: string | "new";
  metadata: TriggerMetadata;
  onClose: () => void;
}) {
  const qc = useQueryClient();
  const isNew = triggerId === "new";

  const detailQ = useQuery({
    queryKey: ["admin", "trigger", triggerId],
    queryFn: () => triggerApi.get(triggerId as string),
    enabled: !isNew,
  });

  const queuesQ = useQuery({ queryKey: ["taxonomy", "queues"], queryFn: () => taxonomyApi.queues.list() });
  const statusesQ = useQuery({ queryKey: ["taxonomy", "statuses"], queryFn: () => taxonomyApi.statuses.list() });
  const prioritiesQ = useQuery({ queryKey: ["taxonomy", "priorities"], queryFn: () => taxonomyApi.priorities.list() });
  const categoriesQ = useQuery({ queryKey: ["taxonomy", "categories"], queryFn: () => taxonomyApi.categories.list() });

  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [isActive, setIsActive] = React.useState(false);
  const [activatorPair, setActivatorPair] = React.useState("action:selective");
  const [locale, setLocale] = React.useState("");
  const [timezone, setTimezone] = React.useState("");
  const [note, setNote] = React.useState("");
  const [conditions, setConditions] = React.useState<ConditionGroup>({ op: "AND", items: [] });
  const [expert, setExpert] = React.useState(false);
  const [actions, setActions] = React.useState<TriggerAction[]>([]);

  React.useEffect(() => {
    if (isNew) {
      setName("");
      setDescription("");
      setIsActive(false);
      setActivatorPair("action:selective");
      setLocale("");
      setTimezone("");
      setNote("");
      setConditions({ op: "AND", items: [] });
      setExpert(false);
      setActions([]);
      return;
    }
    if (!detailQ.data) return;
    hydrate(detailQ.data);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [detailQ.data, isNew]);

  function hydrate(d: TriggerDetail) {
    setName(d.name);
    setDescription(d.description ?? "");
    setIsActive(d.isActive);
    setActivatorPair(`${d.activatorKind}:${d.activatorMode}`);
    setLocale(d.locale ?? "");
    setTimezone(d.timezone ?? "");
    setNote(d.note ?? "");
    const parsed = parseConditions(d.conditionsJson);
    setConditions(parsed);
    setExpert(isExpertConditions(parsed));
    const parsedActions = parseActions(d.actionsJson);
    // Coerce unknown kinds to a noop action so the editor doesn't crash —
    // the validator will block save until the admin replaces or removes
    // the unknown entry.
    setActions(
      parsedActions.map((a): TriggerAction =>
        metadata.actionKinds.includes(a.kind)
          ? (a as TriggerAction)
          : blankActionForKind("set_priority"),
      ),
    );
  }

  const save = useMutation({
    mutationFn: async () => {
      const [activatorKind, activatorMode] = activatorPair.split(":") as [
        TriggerInput["activatorKind"],
        TriggerInput["activatorMode"],
      ];
      const body: TriggerInput = {
        name: name.trim(),
        description: description.trim(),
        isActive,
        activatorKind,
        activatorMode,
        conditionsJson: JSON.stringify(conditions),
        actionsJson: serializeActions(actions),
        locale: locale.trim() || null,
        timezone: timezone.trim() || null,
        note,
      };
      if (isNew) return triggerApi.create(body);
      return triggerApi.update(triggerId as string, body);
    },
    onSuccess: () => {
      toast.success(isNew ? "Trigger created" : "Trigger saved");
      qc.invalidateQueries({ queryKey: ["admin", "triggers"] });
      qc.invalidateQueries({ queryKey: ["admin", "trigger", triggerId] });
      onClose();
    },
    onError: (err: unknown) => {
      const msg = err instanceof Error ? err.message : "Save failed";
      toast.error(msg);
    },
  });

  const taxonomies = {
    queues: queuesQ.data ?? [],
    statuses: statusesQ.data ?? [],
    priorities: prioritiesQ.data ?? [],
    categories: categoriesQ.data ?? [],
  };

  if (!isNew && detailQ.isLoading) {
    return (
      <div className="mt-6 space-y-3">
        <Skeleton className="h-8 w-full" />
        <Skeleton className="h-32 w-full" />
        <Skeleton className="h-48 w-full" />
      </div>
    );
  }

  const canSave = name.trim().length > 0 && !save.isPending;

  return (
    <div className="mt-6 flex flex-col gap-6 pb-24">
      {/* Header */}
      <Section title="Header" hint="Naming, default state, locale and timezone for date-formatting in templates.">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label="Name">
            <Input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="010 - Auto-route Sales"
              autoFocus
            />
          </Field>
          <Field label="Active">
            <div className="flex h-9 items-center">
              <Switch checked={isActive} onCheckedChange={setIsActive} />
              <span className="ml-2 text-xs text-muted-foreground">
                {isActive ? "Will fire on matching events" : "Disabled — saved but inert"}
              </span>
            </div>
          </Field>
        </div>
        <Field label="Description">
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="What does this trigger do, and why was it added?"
            rows={2}
            className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
          />
        </Field>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label="Locale (override)">
            <Input
              value={locale}
              onChange={(e) => setLocale(e.target.value)}
              placeholder="en-US, nl-BE, …"
            />
          </Field>
          <Field label="Timezone (override)">
            <Input
              value={timezone}
              onChange={(e) => setTimezone(e.target.value)}
              placeholder="Europe/Brussels, UTC, …"
            />
          </Field>
        </div>
      </Section>

      {/* Activator */}
      <Section title="Activator" hint="When the trigger is evaluated.">
        <Field label="Activator">
          <select
            value={activatorPair}
            onChange={(e) => setActivatorPair(e.target.value)}
            className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-ring [&_option]:bg-zinc-900"
          >
            {metadata.activatorPairs.map((p) => (
              <option key={p} value={p}>{prettifyActivator(p)}</option>
            ))}
          </select>
        </Field>
      </Section>

      {/* Conditions */}
      <Section title="Conditions" hint="Which tickets match.">
        <ConditionsEditor
          value={conditions}
          onChange={setConditions}
          fields={metadata.conditionFields}
          operators={metadata.conditionOperators}
          maxDepth={metadata.maxConditionDepth}
          expert={expert}
          onExpertChange={setExpert}
          taxonomies={taxonomies}
        />
      </Section>

      {/* Actions */}
      <Section title="Actions" hint="What the trigger does — applied in order, top to bottom.">
        <ActionsEditor
          value={actions}
          onChange={setActions}
          taxonomies={taxonomies}
          variables={metadata.templateVariables}
        />
      </Section>

      {/* Test runner — only for saved triggers */}
      {!isNew && detailQ.data && (
        <Section
          title="Test run"
          hint="Pick a real ticket and preview what this trigger would do — no changes are applied."
        >
          <TestRunner
            triggerId={triggerId as string}
            dirty={isDirty(detailQ.data, {
              name, description, isActive, activatorPair, locale, timezone, note, conditions, actions,
            })}
          />
        </Section>
      )}

      {/* Run history link — only for saved triggers */}
      {!isNew && detailQ.data && (
        <Link
          to="/settings/triggers/$triggerId/runs"
          params={{ triggerId: triggerId as string }}
          onClick={onClose}
          className="flex items-center gap-2 rounded-lg border border-white/[0.06] bg-white/[0.02] px-4 py-3 text-sm text-muted-foreground transition-colors hover:bg-white/[0.04] hover:text-foreground"
        >
          <History className="h-4 w-4" />
          <span className="flex-1">View run history</span>
          <span className="text-xs text-muted-foreground/60">→</span>
        </Link>
      )}

      {/* Sticky footer */}
      <div className="sticky bottom-0 -mx-6 mt-2 flex items-center justify-end gap-2 border-t border-white/10 bg-[hsl(240_10%_5%/0.95)] px-6 py-3 backdrop-blur-xl">
        <Button variant="ghost" onClick={onClose}>
          <X className="h-4 w-4" />
          Cancel
        </Button>
        <Button
          onClick={() => save.mutate()}
          disabled={!canSave}
          className="bg-gradient-to-r from-violet-600 to-indigo-600 hover:from-violet-500 hover:to-indigo-500 text-white shadow-[0_0_20px_rgba(124,58,237,0.3)]"
        >
          <Save className="h-4 w-4" />
          {save.isPending ? "Saving…" : "Save"}
        </Button>
      </div>
    </div>
  );
}

function Section({
  title,
  hint,
  children,
}: {
  title: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-4 py-4">
      <header className="mb-3 space-y-0.5">
        <h3 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          {title}
        </h3>
        {hint && <p className="text-xs text-muted-foreground/70">{hint}</p>}
      </header>
      <div className={cn("space-y-3")}>{children}</div>
    </section>
  );
}

function Field({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="space-y-1.5">
      <label className="text-xs font-medium text-muted-foreground">{label}</label>
      {children}
    </div>
  );
}

function isDirty(
  saved: TriggerDetail,
  form: {
    name: string; description: string; isActive: boolean; activatorPair: string;
    locale: string; timezone: string; note: string;
    conditions: ConditionGroup; actions: TriggerAction[];
  },
): boolean {
  if (form.name !== saved.name) return true;
  if (form.description !== (saved.description ?? "")) return true;
  if (form.isActive !== saved.isActive) return true;
  if (form.activatorPair !== `${saved.activatorKind}:${saved.activatorMode}`) return true;
  if (form.locale !== (saved.locale ?? "")) return true;
  if (form.timezone !== (saved.timezone ?? "")) return true;
  if (form.note !== (saved.note ?? "")) return true;
  if (JSON.stringify(form.conditions) !== JSON.stringify(parseConditions(saved.conditionsJson))) return true;
  if (JSON.stringify(form.actions) !== JSON.stringify(parseActions(saved.actionsJson))) return true;
  return false;
}

function prettifyActivator(pair: string): string {
  const [kind, mode] = pair.split(":");
  const modeLabels: Record<string, string> = {
    selective: "On ticket update — selective (only when referenced fields change)",
    always: "On ticket update — always (every matching mutation)",
    reminder: "When pending-till elapses",
    escalation: "When SLA deadline elapses",
    escalation_warning: "When SLA warning threshold elapses",
  };
  return modeLabels[mode] ?? `${kind}:${mode}`;
}
