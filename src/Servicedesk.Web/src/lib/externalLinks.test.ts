import { beforeAll, describe, expect, it } from "vitest";
import DOMPurify from "dompurify";
import { installExternalLinkTargets } from "@/lib/externalLinks";

function sanitize(html: string): string {
  return DOMPurify.sanitize(html) as unknown as string;
}

describe("installExternalLinkTargets", () => {
  beforeAll(() => {
    installExternalLinkTargets();
  });

  it("opens absolute http(s) links in a new tab with a safe rel", () => {
    const out = sanitize('<p><a href="https://example.com/x">site</a></p>');
    expect(out).toContain('target="_blank"');
    expect(out).toContain('rel="noopener noreferrer"');
    expect(out).toContain('href="https://example.com/x"');
  });

  it("re-stamps target even when the sender omitted or DOMPurify stripped it", () => {
    // DOMPurify's default allow-list drops `target`; the hook runs after that.
    const out = sanitize('<a href="http://example.com" target="_self">x</a>');
    expect(out).toContain('target="_blank"');
  });

  it("leaves relative in-app links navigating in the current tab", () => {
    const out = sanitize('<a href="/kb/articles/123">article</a>');
    expect(out).not.toContain("target=");
  });

  it("leaves mailto links untouched", () => {
    const out = sanitize('<a href="mailto:someone@example.com">mail</a>');
    expect(out).not.toContain("target=");
  });

  it("does not touch anchors without an href", () => {
    const out = sanitize("<a name=\"anchor\">here</a>");
    expect(out).not.toContain("target=");
  });
});
