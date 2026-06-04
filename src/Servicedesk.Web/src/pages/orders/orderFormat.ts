// Shared formatting helpers for the Adsolut Orders overview + detail.

export function formatDate(iso: string | null | undefined): string {
  if (!iso) return "—";
  try {
    return new Intl.DateTimeFormat(undefined, { year: "numeric", month: "short", day: "2-digit" }).format(new Date(iso));
  } catch {
    return iso;
  }
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  try {
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}

export function formatMoney(value: number | null | undefined, currency: string | null | undefined): string {
  if (value === null || value === undefined) return "—";
  try {
    return new Intl.NumberFormat("nl-BE", { style: "currency", currency: currency || "EUR" }).format(value);
  } catch {
    return value.toFixed(2);
  }
}

/// Trim trailing zeros from an Adsolut quantity/decimal (stored at high
/// precision) so "1.00000000" reads as "1" and "3.50000000" as "3.5".
export function formatNumber(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return Number(value)
    .toLocaleString("nl-BE", { maximumFractionDigits: 4 });
}

/// Discount as a percentage label ("10%") — Adsolut stores discount1 as a
/// percentage value. Blank ("—") when null/zero.
export function formatDiscount(value: number | null | undefined): string {
  if (value === null || value === undefined || value === 0) return "—";
  return `${formatNumber(value)}%`;
}
