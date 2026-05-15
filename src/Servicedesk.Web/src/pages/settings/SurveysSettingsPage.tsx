import { useNavigate } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  BarChart3,
  Pencil,
  Plus,
  Smile,
  Trash2,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { surveysApi, type SurveySummary } from "@/lib/surveys-api";

const LIST_QUERY_KEY = ["surveys", "list"] as const;

export function SurveysSettingsPage() {
  const navigate = useNavigate();
  const qc = useQueryClient();
  const listQ = useQuery({
    queryKey: LIST_QUERY_KEY,
    queryFn: () => surveysApi.list(true),
  });

  const remove = useMutation({
    mutationFn: (id: string) => surveysApi.remove(id),
    onSuccess: () => {
      toast.success("Survey deleted.");
      qc.invalidateQueries({ queryKey: LIST_QUERY_KEY });
    },
    onError: (err) => {
      const e = err as Error & { payload?: { error?: string } };
      toast.error(e.payload?.error ?? "Could not delete survey.");
    },
  });

  const rows = listQ.data ?? [];

  return (
    <div className="flex flex-col gap-6">
      <header className="space-y-2">
        <div className="mb-2 text-primary">
          <Smile className="h-6 w-6" />
        </div>
        <h1 className="text-display-md font-semibold text-foreground">Surveys</h1>
        <p className="max-w-xl text-sm text-muted-foreground">
          Build customer satisfaction surveys with rating scales, NPS,
          single/multi choice and free text. Wire them up via the
          <span className="font-medium"> send_survey</span> trigger action or
          attach one to a compose template — it fires automatically on send.
        </p>
      </header>

      <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
        <header className="mb-4 flex items-center justify-between gap-4">
          <div className="space-y-1">
            <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
              Surveys
            </h2>
            <p className="text-xs text-muted-foreground">
              Inactive surveys stay listed so an admin can re-enable them
              without losing historical responses. Surveys with responses
              cannot be hard-deleted.
            </p>
          </div>
          <Button
            onClick={() => navigate({ to: "/settings/surveys-new" })}
            size="sm"
          >
            <Plus className="h-4 w-4" />
            New survey
          </Button>
        </header>

        {listQ.isLoading && <Skeleton className="h-12 w-full" />}
        {listQ.isError && (
          <p className="text-sm text-red-300">Could not load surveys.</p>
        )}
        {!listQ.isLoading && rows.length === 0 && (
          <p className="text-sm text-muted-foreground">
            No surveys yet. Create one and attach it to a trigger or a compose
            template to start collecting feedback.
          </p>
        )}

        <ul className="flex flex-col gap-2">
          {rows.map((s) => (
            <SurveyRow
              key={s.id}
              survey={s}
              onEdit={() =>
                navigate({
                  to: "/settings/surveys/$surveyId",
                  params: { surveyId: s.id },
                })
              }
              onResults={() =>
                navigate({
                  to: "/settings/surveys/$surveyId/results",
                  params: { surveyId: s.id },
                })
              }
              onDelete={() => {
                if (s.responseCount > 0) {
                  toast.error(
                    "This survey has responses. Deactivate it instead of deleting.",
                  );
                  return;
                }
                if (
                  confirm(
                    `Delete '${s.name}'? This cannot be undone.`,
                  )
                ) {
                  remove.mutate(s.id);
                }
              }}
            />
          ))}
        </ul>
      </section>
    </div>
  );
}

function SurveyRow({
  survey,
  onEdit,
  onResults,
  onDelete,
}: {
  survey: SurveySummary;
  onEdit: () => void;
  onResults: () => void;
  onDelete: () => void;
}) {
  return (
    <li
      className={cn(
        "flex items-center justify-between gap-4 rounded-lg border px-4 py-3",
        survey.isActive
          ? "border-white/[0.06] bg-white/[0.02]"
          : "border-white/[0.04] bg-white/[0.01] opacity-70",
      )}
    >
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="truncate text-sm font-medium text-foreground">
            {survey.name}
          </span>
          {!survey.isActive && (
            <span className="rounded-full bg-white/10 px-2 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
              Inactive
            </span>
          )}
          {survey.agentQuestionCount > 0 && (
            <span className="rounded-full bg-primary/15 px-2 py-0.5 text-[10px] uppercase tracking-wider text-primary-foreground/80">
              {survey.agentQuestionCount} per-agent Q
              {survey.agentQuestionCount === 1 ? "" : "s"}
            </span>
          )}
        </div>
        {survey.description && (
          <p className="truncate text-xs text-muted-foreground">
            {survey.description}
          </p>
        )}
        <p className="mt-1 text-[11px] text-muted-foreground/60">
          {survey.questionCount} question{survey.questionCount === 1 ? "" : "s"}
          {" · "}
          {survey.invitationCount} sent
          {" · "}
          {survey.responseCount} response{survey.responseCount === 1 ? "" : "s"}
          {survey.ttlDays ? ` · ${survey.ttlDays} day TTL` : " · default TTL"}
        </p>
      </div>
      <div className="flex items-center gap-2">
        <Button
          variant="ghost"
          size="sm"
          onClick={onResults}
          aria-label={`Results for ${survey.name}`}
        >
          <BarChart3 className="h-4 w-4" />
          Results
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={onEdit}
          aria-label={`Edit ${survey.name}`}
        >
          <Pencil className="h-4 w-4" />
          Edit
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={onDelete}
          aria-label={`Delete ${survey.name}`}
        >
          <Trash2 className="h-4 w-4" />
          Delete
        </Button>
      </div>
    </li>
  );
}
