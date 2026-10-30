/// Reader-side outline for KB articles: gives every h2/h3 a stable anchor id
/// and returns the heading list for the "On this page" panel. Runs on the
/// already-sanitized body; ids are never stored (the server sanitizer drops
/// `id`), so they are recomputed on every render from the heading text.
///
/// Ids carry a `kb-` prefix so DOMPurify's anti-clobbering pass (which
/// removes ids such as "location" or "forms") never strips them.

export type KbHeading = { id: string; text: string; level: 2 | 3 };

export type KbOutline = { html: string; headings: KbHeading[] };

export function slugifyHeading(text: string): string {
  const slug = text
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 60)
    .replace(/-+$/g, "");
  return `kb-${slug || "section"}`;
}

export function buildKbOutline(html: string): KbOutline {
  if (!html || !/<h[23][\s>]/i.test(html)) return { html, headings: [] };

  const doc = new DOMParser().parseFromString(`<body>${html}</body>`, "text/html");
  const used = new Map<string, number>();
  const headings: KbHeading[] = [];

  doc.body.querySelectorAll("h2, h3").forEach((el) => {
    const text = (el.textContent ?? "").replace(/\s+/g, " ").trim();
    if (!text) return;
    const base = slugifyHeading(text);
    const seen = used.get(base) ?? 0;
    used.set(base, seen + 1);
    const id = seen === 0 ? base : `${base}-${seen + 1}`;
    el.id = id;
    headings.push({ id, text, level: el.tagName === "H2" ? 2 : 3 });
  });

  return { html: doc.body.innerHTML, headings };
}
