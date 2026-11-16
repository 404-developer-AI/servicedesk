/// Helpers for the tag hover preview (components/TaggedNoteHoverCard).

/// The event types a personal or group tag can point to — mirrors the
/// server's allow-list on GET …/events/{eventId}/preview. Other rows in the
/// tag lists (checklist blocks, trigger notices) get no hover.
const PREVIEWABLE = new Set(["Note", "Comment", "Call", "MailSent"]);

/// For the PopoverContent that lists the tags: the hover card is portalled
/// outside the popover, so a click or text selection in it would otherwise
/// count as "outside" and close the list.
export function keepOpenForTagPreview(e: Event): void {
  const target = e.target as Element | null;
  if (target?.closest?.("[data-tag-note-preview]")) e.preventDefault();
}

export function isTagPreviewable(eventType: string | null | undefined): boolean {
  return !!eventType && PREVIEWABLE.has(eventType);
}

// ---- "a tag preview is open" ----------------------------------------------
// While a sidebar tag preview is open the content area is blurred behind it.
// The flag lives on <html> (data-tag-preview-open) so plain CSS drives the
// scrim in AppShell — no React state, no re-render of the shell. Tracked per
// card id so a row that unmounts while open (acknowledged, list refreshed)
// can never leave the page blurred.
const openPreviews = new Set<string>();

export function setTagPreviewOpen(id: string, open: boolean): void {
  if (open) openPreviews.add(id);
  else openPreviews.delete(id);
  if (typeof document !== "undefined") {
    document.documentElement.toggleAttribute("data-tag-preview-open", openPreviews.size > 0);
  }
}
