import { describe, expect, it } from "vitest";
import DOMPurify from "dompurify";
import { installRemoteImageBlock } from "@/lib/remoteImages";

installRemoteImageBlock();

function render(html: string): HTMLElement {
  const div = document.createElement("div");
  div.innerHTML = DOMPurify.sanitize(html) as unknown as string;
  return div;
}

describe("installRemoteImageBlock", () => {
  it("removes the source of an external image and keeps its size", () => {
    const img = render('<img src="https://tracker.example.com/p.gif" width="120" height="40">').querySelector("img")!;
    expect(img.getAttribute("src")).toBeNull();
    expect(img.getAttribute("data-remote-src")).toBe("https://tracker.example.com/p.gif");
    expect(img.getAttribute("width")).toBe("120");
    expect(img.getAttribute("alt")).toBe("[external image]");
  });

  it("treats protocol-relative URLs as external", () => {
    const img = render('<img src="//cdn.example.com/logo.png" alt="Logo">').querySelector("img")!;
    expect(img.getAttribute("src")).toBeNull();
    expect(img.getAttribute("alt")).toBe("Logo");
  });

  it("leaves same-origin and data images alone", () => {
    const html = render(
      `<img src="/api/tickets/1/attachments/2"><img src="${window.location.origin}/x.png"><img src="data:image/png;base64,AAAA">`,
    );
    const srcs = Array.from(html.querySelectorAll("img")).map((i) => i.getAttribute("src"));
    expect(srcs).toEqual(["/api/tickets/1/attachments/2", `${window.location.origin}/x.png`, "data:image/png;base64,AAAA"]);
  });

  it("drops remote CSS backgrounds but keeps local ones", () => {
    const el = render(
      '<div style="background-image: url(https://x.example.com/bg.png); color: red">a</div><div style="background: url(/local.png)">b</div>',
    );
    const [a, b] = Array.from(el.querySelectorAll("div"));
    expect(a.getAttribute("style")).not.toContain("x.example.com");
    expect(a.getAttribute("style")).toContain("color: red");
    expect(b.getAttribute("style")).toContain("/local.png");
  });
});
