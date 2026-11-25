import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertCircle, CalendarCheck, CalendarClock, CalendarX, CheckCircle2, Clock, Loader2, Send } from "lucide-react";
import { Button } from "@/components/ui/button";
import { PublicShell } from "@/components/PublicShell";
import { ApiError, apiErrorMessage } from "@/lib/http";
import { minuteToTime, timeToMinute } from "@/lib/timeOfDay";
import { cn } from "@/lib/utils";
import { publicCampaignAnswersApi, type CampaignAnswerView, type CampaignDecision } from "@/lib/campaigns-api";

const MAX_NOTE = 2000;

const CHOICES: { id: CampaignDecision; label: string; hint: string; icon: typeof CalendarCheck }[] = [
  { id: "Approved", label: "Approve", hint: "The proposed moment works for us.", icon: CalendarCheck },
  { id: "OtherTime", label: "Another moment", hint: "Pick the moment that would suit you better.", icon: CalendarClock },
  { id: "Declined", label: "Decline", hint: "We do not want this to go ahead.", icon: CalendarX },
];

/// v0.1.67 (phase 2) — the customer's answer to a campaign mail, opened from
/// `{{campaign.approvalLink}}`. No session: the token in the URL is the only
/// credential and the server shows only the campaign name, the ticket number
/// and the proposed moment. The customer picks Approve / Another moment /
/// Decline with an optional note, and can change the answer until the link
/// expires. A plain mail reply keeps working as well.
export function PublicCampaignAnswerPage({ token }: { token: string }) {
  const qc = useQueryClient();
  const key = ["public-campaign-answer", token] as const;
  const view = useQuery({ queryKey: key, queryFn: () => publicCampaignAnswersApi.get(token), retry: false });
  const [choice, setChoice] = React.useState<CampaignDecision | null>(null);
  const [note, setNote] = React.useState("");
  const [preferredDate, setPreferredDate] = React.useState("");
  const [preferredTime, setPreferredTime] = React.useState("");
  const [editing, setEditing] = React.useState(false);

  React.useEffect(() => {
    if (!view.data) return;
    setChoice(view.data.answer);
    setNote(view.data.note ?? "");
    setPreferredDate(view.data.preferredDate ?? "");
    setPreferredTime(view.data.preferredMinute !== null ? minuteToTime(view.data.preferredMinute) : "");
  }, [view.data]);

  const submit = useMutation({
    mutationFn: () => {
      const other = choice === "OtherTime";
      return publicCampaignAnswersApi.submit(token, choice!, note.trim(),
        other ? preferredDate : null, other && preferredTime ? timeToMinute(preferredTime) : null);
    },
    onSuccess: (data) => { qc.setQueryData(key, data); setEditing(false); },
    // The link ran out while the page was open: show the expired state.
    onError: (err) => { if (err instanceof ApiError && err.status === 410) void qc.invalidateQueries({ queryKey: key }); },
  });

  if (view.isLoading) {
    return (
      <PublicShell>
        <div className="flex items-center gap-3 text-muted-foreground">
          <Loader2 className="h-5 w-5 animate-spin" /> <span>Loading…</span>
        </div>
      </PublicShell>
    );
  }
  if (view.isError || !view.data) {
    const status = view.error instanceof ApiError ? view.error.status : 0;
    return (
      <PublicShell>
        <div className="flex flex-col items-start gap-3">
          {status === 410 ? <Clock className="h-6 w-6 text-muted-foreground" /> : <AlertCircle className="h-6 w-6 text-muted-foreground" />}
          <h1 className="text-xl font-semibold text-foreground">
            {status === 410 ? "This link has expired" : "This link is not valid"}
          </h1>
          <p className="text-sm text-muted-foreground">
            {status === 410
              ? "The answer period for this request has ended. Reply to the original mail if you still want to let us know."
              : "Check that you opened the complete link from the mail, or simply reply to the mail."}
          </p>
        </div>
      </PublicShell>
    );
  }

  const data: CampaignAnswerView = view.data;
  const answered = !!data.answer && !editing;
  // "Another moment" needs a date inside the server's bounds (strings compare as yyyy-MM-dd).
  const dateValid = choice !== "OtherTime"
    || (!!preferredDate && preferredDate >= data.preferredMinDate && preferredDate <= data.preferredMaxDate);
  const submitError = submit.error ? (apiErrorMessage(submit.error) ?? "Your answer could not be saved. Please try again.") : null;

  return (
    <PublicShell>
      <div className="sd-campaign-answer space-y-6">
        <header className="space-y-1">
          <p className="text-xs uppercase tracking-wider text-muted-foreground/70">Ticket #{data.ticketNumber}</p>
          <h1 className="text-2xl font-semibold text-foreground">{data.campaignName}</h1>
          {data.proposedText && (
            <p className="text-sm text-muted-foreground">
              Proposed moment: <span className="font-medium text-foreground">{data.proposedText}</span>
            </p>
          )}
        </header>

        {answered ? (
          <section className="space-y-3">
            <div className="flex items-start gap-3">
              <CheckCircle2 className="mt-0.5 h-6 w-6 shrink-0 text-primary" />
              <div>
                <p className="font-medium text-foreground">
                  Thank you — your answer: {CHOICES.find((c) => c.id === data.answer)?.label}
                </p>
                {data.answer === "OtherTime" && data.preferredText && (
                  <p className="mt-1 text-sm text-foreground">Preferred moment: <span className="font-medium">{data.preferredText}</span></p>
                )}
                {data.note && <p className="mt-1 whitespace-pre-line text-sm text-muted-foreground">{data.note}</p>}
                {data.answeredText && (
                  <p className="mt-1 text-xs text-muted-foreground/70">Received {data.answeredText}</p>
                )}
              </div>
            </div>
            <p className="text-xs text-muted-foreground">
              You can change your answer until {data.expiresText}.
            </p>
            <Button variant="outline" onClick={() => setEditing(true)}>Change my answer</Button>
          </section>
        ) : (
          <form
            className="space-y-5"
            onSubmit={(e) => { e.preventDefault(); if (choice) submit.mutate(); }}
          >
            <fieldset className="space-y-2">
              <legend className="mb-1 text-sm font-medium text-foreground">Your answer</legend>
              {CHOICES.map((c) => (
                <label
                  key={c.id}
                  className={cn(
                    "flex cursor-pointer items-start gap-3 rounded-lg border px-4 py-3 transition-colors",
                    choice === c.id ? "border-primary/50 bg-primary/10" : "border-glass-strong bg-glass hover:bg-glass-hover",
                  )}
                >
                  <input
                    type="radio"
                    name="choice"
                    value={c.id}
                    checked={choice === c.id}
                    onChange={() => setChoice(c.id)}
                    className="mt-1"
                  />
                  <c.icon className={cn("mt-0.5 h-5 w-5 shrink-0", choice === c.id ? "text-primary" : "text-muted-foreground")} />
                  <span>
                    <span className="block text-sm font-medium text-foreground">{c.label}</span>
                    <span className="block text-xs text-muted-foreground">{c.hint}</span>
                  </span>
                </label>
              ))}
            </fieldset>

            {choice === "OtherTime" && (
              <div className="space-y-3 rounded-lg border border-glass-strong bg-glass px-4 py-3">
                {data.otherTimeNotice && (
                  <p className="whitespace-pre-line text-sm text-foreground">{data.otherTimeNotice}</p>
                )}
                <div className="flex flex-wrap gap-3">
                  <label className="space-y-1.5">
                    <span className="block text-sm font-medium text-foreground">Preferred date</span>
                    <input
                      type="date"
                      required
                      value={preferredDate}
                      min={data.preferredMinDate}
                      max={data.preferredMaxDate}
                      onChange={(e) => setPreferredDate(e.target.value)}
                      className="h-9 rounded-md border border-glass-strong bg-glass px-3 text-sm text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring"
                    />
                  </label>
                  <label className="space-y-1.5">
                    <span className="block text-sm font-medium text-foreground">Time <span className="font-normal text-muted-foreground">(optional)</span></span>
                    <input
                      type="time"
                      value={preferredTime}
                      onChange={(e) => setPreferredTime(e.target.value)}
                      className="h-9 rounded-md border border-glass-strong bg-glass px-3 text-sm text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring"
                    />
                  </label>
                </div>
              </div>
            )}

            <label className="block space-y-1.5">
              <span className="text-sm font-medium text-foreground">Note <span className="font-normal text-muted-foreground">(optional)</span></span>
              <textarea
                value={note}
                maxLength={MAX_NOTE}
                rows={4}
                onChange={(e) => setNote(e.target.value)}
                placeholder={choice === "OtherTime" ? "Anything else about the moment, e.g. morning only?" : "Anything we should know?"}
                className="w-full rounded-md border border-glass-strong bg-glass px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
              />
            </label>

            {submitError && <p className="text-sm text-destructive">{submitError}</p>}

            <div className="flex items-center justify-between gap-3">
              <p className="text-xs text-muted-foreground/70">You can change your answer until {data.expiresText}.</p>
              <div className="flex gap-2">
                {editing && (
                  <Button type="button" variant="ghost" onClick={() => {
                    setEditing(false); setChoice(data.answer); setNote(data.note ?? "");
                    setPreferredDate(data.preferredDate ?? "");
                    setPreferredTime(data.preferredMinute !== null ? minuteToTime(data.preferredMinute) : "");
                  }}>
                    Cancel
                  </Button>
                )}
                <Button type="submit" disabled={!choice || !dateValid || submit.isPending} className="min-w-32">
                  {submit.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
                  <span className="ml-1.5">Send answer</span>
                </Button>
              </div>
            </div>
          </form>
        )}
      </div>
    </PublicShell>
  );
}
