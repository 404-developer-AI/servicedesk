import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Save } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { RichTextEditor } from "@/components/RichTextEditor";
import { PendingTillField } from "@/components/PendingTillField";
import { TokenVariableMenu } from "@/components/TokenVariableMenu";
import { composeTemplatesApi } from "@/lib/composeTemplates-api";
import { errorMessage } from "@/lib/http";
import { campaignKeys, campaignsApi, type CampaignDetail } from "@/lib/campaigns-api";

type EditorHandle = {
  chain: () => { focus: () => { insertContent: (content: string) => { run: () => void } } };
  commands: { setContent: (html: string) => void };
};

/// v0.1.67 — the campaign message: subject + body with `{{tokens}}`
/// (contact, company, ticket, plus the campaign's proposed date), optionally
/// started from a mail template, and the default proposed date. Each round
/// freezes the message, so an edit only affects rounds sent afterwards.
export function CampaignComposer({ campaign, readOnly }: { campaign: CampaignDetail; readOnly: boolean }) {
  const qc = useQueryClient();
  const [subject, setSubject] = React.useState(campaign.subject);
  const [bodyHtml, setBodyHtml] = React.useState(campaign.bodyHtml);
  const [templateId, setTemplateId] = React.useState<string | null>(campaign.sourceTemplateId);
  const [defaultProposed, setDefaultProposed] = React.useState<string | null>(campaign.defaultProposedUtc);
  const [otherTimeNotice, setOtherTimeNotice] = React.useState(campaign.otherTimeNotice);
  const editorRef = React.useRef<EditorHandle | null>(null);
  const subjectRef = React.useRef<HTMLInputElement | null>(null);
  const lastField = React.useRef<"subject" | "body">("body");

  React.useEffect(() => {
    setSubject(campaign.subject);
    setBodyHtml(campaign.bodyHtml);
    setTemplateId(campaign.sourceTemplateId);
    setDefaultProposed(campaign.defaultProposedUtc);
    setOtherTimeNotice(campaign.otherTimeNotice);
  }, [campaign.id, campaign.subject, campaign.bodyHtml, campaign.sourceTemplateId, campaign.defaultProposedUtc, campaign.otherTimeNotice]);

  const tokens = useQuery({ queryKey: campaignKeys.tokens(), queryFn: campaignsApi.tokens, staleTime: Infinity });
  const templates = useQuery({
    queryKey: ["compose-templates", "usable", "mail", "campaigns"],
    queryFn: () => composeTemplatesApi.usable(null, null, "mail"),
    staleTime: 5 * 60_000,
  });

  const dirty = subject !== campaign.subject
    || bodyHtml !== campaign.bodyHtml
    || templateId !== campaign.sourceTemplateId
    || defaultProposed !== campaign.defaultProposedUtc
    || otherTimeNotice !== campaign.otherTimeNotice;

  const save = useMutation({
    mutationFn: () => campaignsApi.update(campaign.id, {
      subject,
      bodyHtml,
      sourceTemplateId: templateId,
      otherTimeNotice,
      ...(defaultProposed ? { defaultProposedUtc: defaultProposed } : { clearDefaultProposed: true }),
    }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: campaignKeys.detail(campaign.id) });
      toast.success("Message saved");
    },
    onError: (err) => toast.error(errorMessage(err, "Could not save the message")),
  });

  const insertToken = (token: string) => {
    if (lastField.current === "subject" && subjectRef.current) {
      const el = subjectRef.current;
      const start = el.selectionStart ?? subject.length;
      const end = el.selectionEnd ?? subject.length;
      setSubject(subject.slice(0, start) + token + subject.slice(end));
      requestAnimationFrame(() => {
        el.focus();
        el.setSelectionRange(start + token.length, start + token.length);
      });
      return;
    }
    editorRef.current?.chain().focus().insertContent(token).run();
  };

  const applyTemplate = (id: string) => {
    const tpl = templates.data?.find((t) => t.id === id);
    if (!tpl) return;
    const previous = { body: bodyHtml, templateId };
    setTemplateId(tpl.id);
    setBodyHtml(tpl.bodyHtml);
    editorRef.current?.commands.setContent(tpl.bodyHtml);
    if (previous.body.replace(/<[^>]*>/g, "").trim()) {
      toast.message(`Message replaced by “${tpl.name}”`, {
        action: {
          label: "Undo",
          onClick: () => {
            setTemplateId(previous.templateId);
            setBodyHtml(previous.body);
            editorRef.current?.commands.setContent(previous.body);
          },
        },
      });
    }
  };

  return (
    <div className="sd-campaign-composer space-y-5">
      <div className="grid gap-4 md:grid-cols-[1fr_16rem]">
        <div className="space-y-1.5">
          <label htmlFor="sd-campaign-subject" className="text-xs font-medium text-muted-foreground">Subject</label>
          <Input
            id="sd-campaign-subject"
            ref={subjectRef}
            value={subject}
            maxLength={500}
            disabled={readOnly}
            onFocus={() => { lastField.current = "subject"; }}
            onChange={(e) => setSubject(e.target.value)}
            placeholder="e.g. Firewall update on {{campaign.proposedDate}}"
          />
          <p className="text-[11px] text-muted-foreground/70">
            The ticket reference is added automatically; replies land on each ticket's own thread.
          </p>
        </div>
        <div className="space-y-1.5">
          <span className="text-xs font-medium text-muted-foreground">Default proposed date</span>
          <PendingTillField
            value={defaultProposed}
            onCommit={setDefaultProposed}
            commitOnChange
            pastMessage="The proposed date must be in the future."
          />
          <p className="text-[11px] text-muted-foreground/70">Used for {"{{campaign.proposedDate}}"} unless a ticket has its own date.</p>
        </div>
      </div>

      <div className="space-y-1.5">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <span className="text-xs font-medium text-muted-foreground">Message</span>
          <div className="flex items-center gap-2">
            <select
              value=""
              aria-label="Start from a template"
              disabled={readOnly || !templates.data?.length}
              onChange={(e) => { if (e.target.value) applyTemplate(e.target.value); }}
              className="h-8 rounded-md border border-glass bg-glass px-2 text-xs text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring [&_option]:bg-popover [&_option]:text-popover-foreground"
            >
              <option value="">Start from a template…</option>
              {(templates.data ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select>
            <TokenVariableMenu tokens={tokens.data ?? []} onPick={insertToken} disabled={readOnly} />
          </div>
        </div>
        <div onFocusCapture={() => { lastField.current = "body"; }}>
          <RichTextEditor
            content={bodyHtml}
            onChange={setBodyHtml}
            editable={!readOnly}
            placeholder="Dear {{contact.firstName}}, we would like to update your firewall on {{campaign.proposedDate}} at {{campaign.proposedTime}}…"
            minHeight="220px"
            maxHeight="520px"
            onEditorReady={(editor) => { editorRef.current = editor as unknown as EditorHandle | null; }}
          />
        </div>
        <p className="text-[11px] text-muted-foreground/70">
          Placeholders are filled in per ticket when a round is sent. A ticket with an empty placeholder is skipped
          instead of mailed with a gap — the preview shows which ones. Your signature is added as on any ticket mail.
        </p>
      </div>

      <div className="space-y-1.5">
        <label htmlFor="sd-campaign-othertime" className="text-xs font-medium text-muted-foreground">
          Note with "Another moment" <span className="font-normal">(optional, shown on the answer page)</span>
        </label>
        <textarea
          id="sd-campaign-othertime"
          rows={2}
          maxLength={1000}
          value={otherTimeNotice}
          disabled={readOnly}
          onChange={(e) => setOtherTimeNotice(e.target.value)}
          placeholder="e.g. An intervention at another moment is charged at 150 % of the normal rate."
          className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring disabled:opacity-60"
        />
      </div>

      {!readOnly && (
        <div className="flex items-center justify-end gap-2">
          {dirty && <span className="text-xs text-muted-foreground">Unsaved changes</span>}
          <Button onClick={() => save.mutate()} disabled={!dirty || save.isPending}>
            {save.isPending ? <Loader2 className="mr-1.5 h-4 w-4 animate-spin" /> : <Save className="mr-1.5 h-4 w-4" />}
            Save message
          </Button>
        </div>
      )}
    </div>
  );
}
