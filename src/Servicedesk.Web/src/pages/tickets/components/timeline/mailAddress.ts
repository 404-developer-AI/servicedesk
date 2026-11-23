export type MailAddr = { address: string; name?: string };

/// Normalises a metadata `to`/`cc`/`bcc` array into address rows, dropping
/// entries without a usable address. Returns [] for anything non-array (e.g.
/// an un-enriched event), so callers can treat length 0 as "nothing to show".
export function parseRecipientList(value: unknown): MailAddr[] {
  if (!Array.isArray(value)) return [];
  return (value as Array<{ address?: unknown; name?: unknown }>)
    .filter((r) => typeof r?.address === "string" && (r.address as string).length > 0)
    .map((r) => ({
      address: r.address as string,
      name:
        typeof r.name === "string" && (r.name as string).length > 0
          ? (r.name as string)
          : undefined,
    }));
}
