/// Pop-out window for the checklist panel (second-monitor workflow). Named
/// per ticket so a second click focuses the existing window instead of
/// opening another; sized like a tall side panel. The pop-out route renders
/// outside AppShell and stays in sync through the same SignalR pushes as
/// the main tab.
export function openChecklistWindow(ticketId: string, checklistId?: string | null): void {
  const url = `/tickets/${encodeURIComponent(ticketId)}/checklists${checklistId ? `?checklist=${encodeURIComponent(checklistId)}` : ""}`;
  const width = 560;
  const height = Math.max(640, Math.min(window.screen.availHeight - 80, 1000));
  const left = Math.max(0, (window.screenX ?? 0) + window.outerWidth - width - 24);
  const top = Math.max(0, (window.screenY ?? 0) + 40);
  const features = `width=${width},height=${height},left=${left},top=${top},menubar=no,toolbar=no,location=no,resizable=yes,scrollbars=yes`;
  const win = window.open(url, `sd-checklist-${ticketId}`, features);
  win?.focus();
}
