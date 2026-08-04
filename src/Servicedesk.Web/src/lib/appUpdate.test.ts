import { describe, expect, it, afterEach } from "vitest";
import { canReloadSafely } from "@/lib/appUpdate";
import { anchorReferenceVersion, getReferenceVersion } from "@/lib/clientVersion";

afterEach(() => {
  document.body.innerHTML = "";
});

describe("canReloadSafely", () => {
  it("is safe on an empty page", () => {
    expect(canReloadSafely()).toBe(true);
  });

  it("is unsafe while a dialog is open", () => {
    document.body.innerHTML = '<div role="dialog">modal</div>';
    expect(canReloadSafely()).toBe(false);
  });

  it("is unsafe while a text input is focused", () => {
    document.body.innerHTML = "<input type='text' />";
    document.querySelector("input")!.focus();
    expect(canReloadSafely()).toBe(false);
  });

  it("is unsafe while a rich-text editor is focused", () => {
    const editor = document.createElement("div");
    editor.setAttribute("tabindex", "0");
    Object.defineProperty(editor, "isContentEditable", { value: true });
    document.body.appendChild(editor);
    editor.focus();
    expect(canReloadSafely()).toBe(false);
  });

  it("is safe when only a button is focused", () => {
    document.body.innerHTML = "<button>ok</button>";
    document.querySelector("button")!.focus();
    expect(canReloadSafely()).toBe(true);
  });
});

describe("reference version anchoring (dev fallback)", () => {
  it("adopts the first observed server version and keeps it", () => {
    // Without a baked __APP_VERSION__ (vitest does not run vite.config.ts)
    // the module starts unanchored.
    expect(getReferenceVersion()).toBeNull();

    anchorReferenceVersion("1.2.3");
    expect(getReferenceVersion()).toBe("1.2.3");

    anchorReferenceVersion("9.9.9");
    expect(getReferenceVersion()).toBe("1.2.3");
  });
});
