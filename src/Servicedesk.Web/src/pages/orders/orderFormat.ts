import { formatMoney, formatNumber } from "@/lib/format";

export { formatMoney, formatNumber };

// Shared formatting helpers for the Adsolut Orders overview + detail.

/// Margin percentage the way Adsolut prints it on the order (whole percent,
/// e.g. "119%"). Blank ("—") when there is no cost to compute against.
export function formatMarginPercent(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return `${Math.round(value).toLocaleString("nl-BE")}%`;
}
