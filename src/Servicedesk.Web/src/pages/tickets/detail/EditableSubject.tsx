import * as React from "react";
import { Check, Pencil, X } from "lucide-react";
import { TicketTypeBadge } from "@/components/TicketTypeBadge";
import { FormHeaderButtons } from "@/components/forms/FormHeaderButtons";
import { FollowButton } from "../components/FollowButton";
import { TicketNumber } from "./TicketHeaderParts";

export function EditableSubject({
  ticketId,
  queueId,
  number,
  value,
  ticketTypeId,
  onSave,
}: {
  ticketId: string;
  queueId: string;
  number: number;
  value: string;
  ticketTypeId?: string | null;
  onSave: (subject: string) => Promise<void>;
}) {
  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState(value);
  const inputRef = React.useRef<HTMLInputElement>(null);

  React.useEffect(() => {
    setDraft(value);
  }, [value]);

  React.useEffect(() => {
    if (editing) inputRef.current?.focus();
  }, [editing]);

  const save = async () => {
    const trimmed = draft.trim();
    if (!trimmed || trimmed === value) {
      setDraft(value);
      setEditing(false);
      return;
    }
    await onSave(trimmed);
    setEditing(false);
  };

  const cancel = () => {
    setDraft(value);
    setEditing(false);
  };

  if (!editing) {
    return (
      <div className="group flex items-center gap-3">
        <TicketNumber number={number} />
        <FollowButton ticketId={ticketId} />
        {ticketTypeId && <TicketTypeBadge ticketTypeId={ticketTypeId} />}
        <h1 className="text-2xl font-semibold tracking-tight text-foreground leading-tight flex-1 min-w-0 truncate">
          {value}
        </h1>
        <button
          type="button"
          onClick={() => setEditing(true)}
          className="shrink-0 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-glass-hover transition-all"
          title="Edit subject"
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
        <FormHeaderButtons ticketId={ticketId} queueId={queueId} />
      </div>
    );
  }

  return (
    <div className="flex items-center gap-3">
      <TicketNumber number={number} />
      <FollowButton ticketId={ticketId} />
      {ticketTypeId && <TicketTypeBadge ticketTypeId={ticketTypeId} />}
      <input
        ref={inputRef}
        type="text"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") save();
          if (e.key === "Escape") cancel();
        }}
        className="flex-1 min-w-0 text-2xl font-semibold tracking-tight text-foreground leading-tight bg-transparent border-b-2 border-primary/60 outline-hidden py-0.5"
      />
      <button
        type="button"
        onClick={save}
        className="shrink-0 p-1.5 rounded-md text-green-400 hover:bg-green-400/10 transition-colors"
        title="Save"
      >
        <Check className="h-4 w-4" />
      </button>
      <button
        type="button"
        onClick={cancel}
        className="shrink-0 p-1.5 rounded-md text-muted-foreground hover:bg-glass-hover transition-colors"
        title="Cancel"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}
