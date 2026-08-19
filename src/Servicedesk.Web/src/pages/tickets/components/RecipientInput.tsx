import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { X } from "lucide-react";
import { contactApi, ticketApi, type MailRecipientInput } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

// Loose email shape check — matches the server's "single @, dotted domain"
// level of strictness without dragging in a full RFC 5322 parser. Pills that
// fail this still commit (the agent may be mid-typing a paste) but render with
// a warning tint so a typo is obvious before send.
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/// Normalise for dedup — lowercase the whole address. Plus-suffix variants are
/// intentionally kept distinct here (unlike the reply-all own-mailbox guard);
/// an agent who explicitly types two plus-addresses means it.
function dedupKey(address: string): string {
  return address.trim().toLowerCase();
}

/// Parse a single typed/pasted token into a recipient. Accepts bare addresses
/// and the `Name <addr@host>` form the reply prefill emits.
function parseOne(raw: string): MailRecipientInput | null {
  const s = raw.trim().replace(/[,;]+$/, "").trim();
  if (!s) return null;
  const m = s.match(/^"?([^"<]+?)"?\s*<([^>]+)>$/);
  if (m) return { address: m[2].trim(), name: m[1].trim() };
  return { address: s };
}

function labelFor(r: MailRecipientInput): string {
  return r.name && r.name !== r.address ? r.name : r.address;
}

type Props = {
  value: MailRecipientInput[];
  onChange: (next: MailRecipientInput[]) => void;
  placeholder?: string;
  ariaLabel?: string;
  autoFocus?: boolean;
  /// When set, suggestions come from the ticket's ranked recipient endpoint
  /// (company contacts + previously-used addresses by usage, general contact
  /// matches below) and the list already opens on focus with an empty input.
  /// Without it the field falls back to the plain contact typeahead.
  ticketId?: string;
};

/// Unified dropdown row — from the ranked endpoint or the legacy contact
/// search, whichever backs this instance.
type Suggestion = { address: string; name: string | null };

/// Chip/pill recipient editor with contact autocomplete. Backs the To/Cc/Bcc
/// fields of the mail composer: type to get contact suggestions, Enter/comma/
/// semicolon/Tab (or picking a suggestion) commits a pill, the × on each pill —
/// or Backspace on an empty input — removes it. With a `ticketId`, focusing
/// an empty field already lists the ticket's company contacts (most-used
/// first — see the recipient-suggestions endpoint); typing re-ranks with
/// company matches above general contact matches.
export function RecipientInput({
  value,
  onChange,
  placeholder,
  ariaLabel,
  autoFocus,
  ticketId,
}: Props) {
  const [input, setInput] = React.useState("");
  const [debounced, setDebounced] = React.useState("");
  const [open, setOpen] = React.useState(false);
  const [activeIndex, setActiveIndex] = React.useState(-1);
  const inputRef = React.useRef<HTMLInputElement>(null);
  const blurTimer = React.useRef<number | null>(null);

  React.useEffect(() => {
    const t = setTimeout(() => setDebounced(input.trim()), 200);
    return () => clearTimeout(t);
  }, [input]);

  // Ranked, ticket-scoped suggestions — fires as soon as the field opens,
  // also with an empty input (that's the "click → company contacts" list).
  const { data: ranked } = useQuery({
    queryKey: ["recipient-suggestions", ticketId, debounced],
    queryFn: () => ticketApi.recipientSuggestions(ticketId!, debounced),
    enabled: !!ticketId && open,
    placeholderData: (prev) => prev,
    staleTime: 30_000,
  });

  // Legacy plain contact typeahead for instances without a ticket context.
  const { data: contacts } = useQuery({
    queryKey: ["contacts", "recipient-suggest", debounced],
    queryFn: () => contactApi.list(debounced),
    enabled: !ticketId && debounced.length >= 1,
    placeholderData: (prev) => prev,
    staleTime: 30_000,
  });

  // Drop contacts already added, and cap the list so the dropdown stays a
  // typeahead rather than a report.
  const taken = React.useMemo(
    () => new Set(value.map((r) => dedupKey(r.address))),
    [value],
  );
  const suggestions = React.useMemo<Suggestion[]>(() => {
    const source: Suggestion[] = ticketId
      ? (ranked?.items ?? []).map((s) => ({ address: s.address, name: s.name }))
      : (contacts ?? [])
          .filter((c) => c.email)
          .map((c) => ({
            address: c.email,
            name: [c.firstName, c.lastName].filter(Boolean).join(" ") || null,
          }));
    return source.filter((s) => !taken.has(dedupKey(s.address))).slice(0, 8);
  }, [ticketId, ranked, contacts, taken]);

  const showDropdown =
    open && suggestions.length > 0 && (!!ticketId || debounced.length >= 1);

  function commitRaw(raw: string) {
    const parts = raw.split(/[,;\n]+/);
    const next = [...value];
    const seen = new Set(value.map((r) => dedupKey(r.address)));
    let added = false;
    for (const part of parts) {
      const parsed = parseOne(part);
      if (!parsed) continue;
      const key = dedupKey(parsed.address);
      if (seen.has(key)) continue;
      seen.add(key);
      next.push(parsed);
      added = true;
    }
    if (added) onChange(next);
    setInput("");
    setActiveIndex(-1);
  }

  function addContact(email: string, name: string) {
    const key = dedupKey(email);
    if (value.some((r) => dedupKey(r.address) === key)) {
      setInput("");
      setActiveIndex(-1);
      return;
    }
    onChange([...value, name ? { address: email, name } : { address: email }]);
    setInput("");
    setActiveIndex(-1);
  }

  function removeAt(index: number) {
    onChange(value.filter((_, i) => i !== index));
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (showDropdown && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
      e.preventDefault();
      setActiveIndex((i) => {
        const last = suggestions.length - 1;
        if (e.key === "ArrowDown") return i >= last ? 0 : i + 1;
        return i <= 0 ? last : i - 1;
      });
      return;
    }
    if (e.key === "Enter" || e.key === "Tab" || e.key === "," || e.key === ";") {
      // Tab only commits when there's something to commit, so it still moves
      // focus on an empty field.
      if (e.key === "Tab" && !input.trim() && activeIndex < 0) return;
      if (showDropdown && activeIndex >= 0) {
        e.preventDefault();
        const s = suggestions[activeIndex];
        addContact(s.address, s.name ?? "");
        return;
      }
      if (input.trim()) {
        e.preventDefault();
        commitRaw(input);
      }
      return;
    }
    if (e.key === "Backspace" && !input && value.length > 0) {
      e.preventDefault();
      removeAt(value.length - 1);
      return;
    }
    if (e.key === "Escape" && open) {
      setOpen(false);
      setActiveIndex(-1);
    }
  }

  function handlePaste(e: React.ClipboardEvent<HTMLInputElement>) {
    const text = e.clipboardData.getData("text");
    if (/[,;\n]/.test(text)) {
      e.preventDefault();
      commitRaw((input ? input + "," : "") + text);
    }
  }

  return (
    <div className="relative flex-1">
      <div
        onClick={() => inputRef.current?.focus()}
        className={cn(
          "flex flex-wrap items-center gap-1 bg-glass border border-glass rounded-md px-1.5 py-1",
          "focus-within:border-glass-strong cursor-text min-h-[34px]",
        )}
      >
        {value.map((r, i) => {
          const invalid = !EMAIL_RE.test(r.address.trim());
          return (
            <span
              key={`${dedupKey(r.address)}-${i}`}
              title={r.address}
              className={cn(
                "inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs max-w-full",
                invalid
                  ? "bg-amber-500/15 text-amber-200 border border-amber-500/30"
                  : "bg-sky-500/15 text-sky-200 border border-sky-500/25",
              )}
            >
              <span className="truncate">{labelFor(r)}</span>
              <button
                type="button"
                onClick={(e) => {
                  e.stopPropagation();
                  removeAt(i);
                }}
                className="shrink-0 opacity-60 hover:opacity-100 transition-opacity"
                aria-label={`Remove ${r.address}`}
              >
                <X className="h-3 w-3" />
              </button>
            </span>
          );
        })}
        <input
          ref={inputRef}
          type="text"
          value={input}
          aria-label={ariaLabel}
          autoFocus={autoFocus}
          onChange={(e) => {
            setInput(e.target.value);
            setOpen(true);
            setActiveIndex(-1);
          }}
          onKeyDown={handleKeyDown}
          onPaste={handlePaste}
          onFocus={() => setOpen(true)}
          onBlur={() => {
            // Defer so a suggestion mousedown registers before we commit/close.
            blurTimer.current = window.setTimeout(() => {
              if (input.trim()) commitRaw(input);
              setOpen(false);
              setActiveIndex(-1);
            }, 120);
          }}
          placeholder={value.length === 0 ? placeholder : ""}
          className="flex-1 min-w-[8rem] bg-transparent px-1 py-0.5 text-sm focus:outline-none"
        />
      </div>

      {showDropdown ? (
        <div
          className="absolute left-0 right-0 top-full z-20 mt-1 max-h-[220px] overflow-y-auto rounded-md bg-popover text-popover-foreground border border-glass p-1 shadow-xl"
          onMouseDown={(e) => {
            // Keep focus on the input so onBlur's deferred commit doesn't fire
            // before the click handler.
            e.preventDefault();
            if (blurTimer.current) window.clearTimeout(blurTimer.current);
          }}
        >
          {suggestions.map((s, i) => (
            <button
              key={dedupKey(s.address)}
              type="button"
              onClick={() => {
                addContact(s.address, s.name ?? "");
                inputRef.current?.focus();
              }}
              onMouseEnter={() => setActiveIndex(i)}
              className={cn(
                "w-full rounded px-2 py-1.5 text-left text-sm transition-colors",
                i === activeIndex ? "bg-glass-strong" : "hover:bg-glass-hover",
              )}
            >
              {s.name ? (
                <div className="truncate font-medium text-foreground">{s.name}</div>
              ) : null}
              <div className="truncate text-xs text-muted-foreground">{s.address}</div>
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
