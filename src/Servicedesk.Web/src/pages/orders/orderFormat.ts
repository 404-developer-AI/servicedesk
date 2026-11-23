import { formatMoney, formatNumber } from "@/lib/format";

export { formatMoney, formatNumber };

// Shared formatting helpers for the Adsolut Orders overview + detail.

/// Discount as a percentage label ("10%") — Adsolut stores discount1 as a
/// percentage value. Blank ("—") when null/zero.
export function formatDiscount(value: number | null | undefined): string {
  if (value === null || value === undefined || value === 0) return "—";
  return `${formatNumber(value)}%`;
}

/// Margin percentage the way Adsolut prints it on the order (whole percent,
/// e.g. "119%"). Blank ("—") when there is no cost to compute against.
export function formatMarginPercent(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return `${Math.round(value).toLocaleString("nl-BE")}%`;
}
