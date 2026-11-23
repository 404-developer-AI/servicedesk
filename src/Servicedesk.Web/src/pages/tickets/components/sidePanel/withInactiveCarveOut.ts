/// Filter a taxonomy list to active rows, but keep the ticket's current
/// selection in the list (pinned to the front) when it has been
/// deactivated — otherwise the <select> renders blank and the user
/// can't save the ticket.
export function withInactiveCarveOut<T extends { id: string; isActive: boolean }>(
  all: T[] | undefined,
  current: T | undefined,
): T[] {
  if (!all) return [];
  const active = all.filter((x) => x.isActive);
  if (current && !current.isActive) return [current, ...active];
  return active;
}
