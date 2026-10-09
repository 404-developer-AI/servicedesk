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
  it("replaces an external image with a placeholder that keeps its size", () => {
    const root = render('<p><img src="https://cdn.example.com/banner.png" width="320" height="80" alt="Spring sale"></p>');
    expect(root.querySelector("img")).toBeNull();
    const ph = root.querySelector(".sd-remote-image")!;
    expect(ph.textContent).toBe("Spring sale");
    expect(ph.getAttribute("role")).toBe("img");
    expect(ph.getAttribute("style")).toBe("width:320px;height:80px");
    expect(root.innerHTML).not.toContain("cdn.example.com");
  });

  it("uses a generic label when the image has no alt text", () => {
    const root = render('<img src="//cdn.example.com/logo.png">');
    expect(root.querySelector("img")).toBeNull();
    expect(root.querySelector(".sd-remote-image")!.textContent).toBe("External image blocked");
  });

  it("drops tracking pixels outright", () => {
    const root = render(
      '<p>a<img src="https://t.example.com/o.gif" width="1" height="1">' +
        '<img src="https://t.example.com/p.gif" style="display: none">' +
        '<img src="https://t.example.com/q.gif" style="width:0px;height:0px">b</p>',
    );
    expect(root.querySelector("img")).toBeNull();
    expect(root.querySelector(".sd-remote-image")).toBeNull();
    expect(root.textContent).toBe("ab");
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
