import { beforeEach, describe, expect, it, vi } from "vitest";

const api = vi.hoisted(() => ({
  getWorkspace: vi.fn(),
  saveWorkspace: vi.fn(),
  deleteWorkspaceKey: vi.fn(),
  fireAndForgetWorkspaceSave: vi.fn(),
}));
vi.mock("@/lib/preferences-api", () => ({ preferencesApi: api }));

import { formDraftKey, useWorkspaceStore, type FormDraftEntry } from "./useWorkspaceStore";

const entry = (over: Partial<FormDraftEntry> = {}): Omit<FormDraftEntry, "updatedUtc"> => ({
  key: "t1:create:tpl1",
  ticketId: "t1",
  ticketNumber: 1234,
  formName: "Laptop intake",
  mode: "create",
  templateId: "tpl1",
  draft: { serial: { kind: "text", value: "SN-1" } },
  baseline: "{}",
  minimized: false,
  ...over,
});

describe("form drafts in the workspace store", () => {
  beforeEach(() => {
    useWorkspaceStore.setState({ formDrafts: {}, pendingFormRestore: null });
    Object.values(api).forEach((f) => f.mockReset());
    api.saveWorkspace.mockResolvedValue(undefined);
    api.deleteWorkspaceKey.mockResolvedValue(undefined);
  });

  it("keys a draft by ticket and template or form", () => {
    expect(formDraftKey({ mode: "create", ticketId: "t1", templateId: "tpl1" })).toBe("t1:create:tpl1");
    expect(formDraftKey({ mode: "edit", ticketId: "t1", formId: "f1" })).toBe("t1:edit:f1");
  });

  it("saves the draft without its window-local minimised flag", async () => {
    useWorkspaceStore.getState().setFormDraft(entry({ minimized: true }));
    await useWorkspaceStore.getState().flush();
    const sent = api.saveWorkspace.mock.calls[0][0] as { key: string; value: string }[];
    const row = sent.find((e) => e.key === "workspace:formdraft:t1:create:tpl1");
    expect(row).toBeDefined();
    expect(JSON.parse(row!.value)).not.toHaveProperty("minimized");
  });

  it("minimises, and removing deletes the server key and a pending restore", () => {
    const s = useWorkspaceStore.getState();
    s.setFormDraft(entry());
    s.setFormDraftMinimized("t1:create:tpl1", true);
    expect(useWorkspaceStore.getState().formDrafts["t1:create:tpl1"].minimized).toBe(true);
    s.requestFormRestore("t1:create:tpl1");
    s.removeFormDraft("t1:create:tpl1");
    expect(useWorkspaceStore.getState().formDrafts).toEqual({});
    expect(useWorkspaceStore.getState().pendingFormRestore).toBeNull();
    expect(api.deleteWorkspaceKey).toHaveBeenCalledWith("workspace:formdraft:t1:create:tpl1");
  });

  it("brings a loaded draft back minimised and skips corrupt rows", async () => {
    const stored = { ...entry(), updatedUtc: "2026-11-16T10:00:00Z" } as Partial<FormDraftEntry>;
    delete stored.minimized;
    api.getWorkspace.mockResolvedValue({
      "workspace:formdraft:t1:create:tpl1": JSON.stringify(stored),
      "workspace:formdraft:broken": "{not json",
    });
    await useWorkspaceStore.getState().loadFromServer();
    const drafts = useWorkspaceStore.getState().formDrafts;
    expect(Object.keys(drafts)).toEqual(["t1:create:tpl1"]);
    expect(drafts["t1:create:tpl1"].minimized).toBe(true);
  });
});
