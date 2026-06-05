import { type ChangeEvent, useRef, useState } from "react";
import {
  ArrowLeft,
  Camera,
  Check,
  Download,
  ImageIcon,
  Pencil,
  PenLine,
  Plus,
  Save,
  Trash2,
  Upload,
  X,
} from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { settingsApi, type SettingEntry } from "@/lib/api";
import { SettingField } from "@/components/settings/SettingField";
import {
  signaturesApi,
  signatureProfileApi,
  teamProfilesApi,
  photoFrameApi,
  FRAME_URL,
  type SignatureSummary,
  type SignatureBundle,
  type MySignatureProfileUpdate,
  type TeamProfile,
  type TeamProfileUpdate,
} from "@/lib/signatures-api";
import { SignatureEditor } from "./signatures/SignatureEditor";
import { PhotoCompositor } from "./signatures/PhotoCompositor";

// ---- query keys ----

const SIGS_LIST_KEY = ["signatures", "list"] as const;
const SIGS_SETTINGS_KEY = ["settings", "list", "Signatures"] as const;
const PROFILE_KEY = ["me", "signature-profile"] as const;
const TEAM_PROFILES_KEY = ["signatures", "team-profiles"] as const;

// ---- setting keys ----

const KEY_ENABLED = "Signatures.Enabled";
const KEY_APPEND_REPLIES = "Signatures.AppendOnReplies";
const KEY_COMPOSER_PRELOAD = "Signatures.ComposerPreload";
const KEY_ENTRA_SYNC = "Signatures.EntraSyncEnabled";
const KEY_ENTRA_PHOTOS = "Signatures.EntraSyncPhotos";
const KEY_DEFAULT_SIG = "Signatures.DefaultSystemSignatureId";

// ---- mode ----

type Mode =
  | { kind: "list" }
  | { kind: "new" }
  | { kind: "edit"; id: string };

// ---- page root ----

export function SignaturesSettingsPage() {
  const [mode, setMode] = useState<Mode>({ kind: "list" });

  return (
    <div className="flex flex-col gap-6">
      <header className="space-y-2">
        <div className="mb-2 text-primary">
          <PenLine className="h-6 w-6" />
        </div>
        <h1 className="text-display-md font-semibold text-foreground">Signatures</h1>
        <p className="max-w-xl text-sm text-muted-foreground">
          Admin-managed email signatures built from a reusable block tree. Assign per mailbox,
          inject agent profile variables, and sync profile photos from Entra ID.
        </p>
      </header>

      {mode.kind === "list" && (
        <ListPanel
          onNew={() => setMode({ kind: "new" })}
          onEdit={(id) => setMode({ kind: "edit", id })}
        />
      )}
      {(mode.kind === "new" || mode.kind === "edit") && (
        <EditorPanel
          id={mode.kind === "edit" ? mode.id : null}
          onDone={() => setMode({ kind: "list" })}
        />
      )}
    </div>
  );
}

// ---- list panel ----

function ListPanel({
  onNew,
  onEdit,
}: {
  onNew: () => void;
  onEdit: (id: string) => void;
}) {
  const qc = useQueryClient();
  const [deleteTarget, setDeleteTarget] = useState<SignatureSummary | null>(null);
  const [exportingId, setExportingId] = useState<string | null>(null);
  const importInputRef = useRef<HTMLInputElement | null>(null);

  const listQ = useQuery({
    queryKey: SIGS_LIST_KEY,
    queryFn: () => signaturesApi.list(),
  });

  const settingsQ = useQuery({
    queryKey: SIGS_SETTINGS_KEY,
    queryFn: () => settingsApi.list("Signatures"),
  });

  const sigsData = listQ.data ?? [];
  const systemSigs = sigsData.filter((s) => s.isSystem);

  const removeMut = useMutation({
    mutationFn: (id: string) => signaturesApi.remove(id),
    onSuccess: () => {
      toast.success("Signature deleted.");
      qc.invalidateQueries({ queryKey: SIGS_LIST_KEY });
      setDeleteTarget(null);
    },
    onError: () => toast.error("Could not delete signature."),
  });

  const importMut = useMutation({
    mutationFn: (bundle: SignatureBundle) => signaturesApi.importBundle(bundle),
    onSuccess: (created) => {
      toast.success(`Imported "${created.name}". It's disabled — assign a mailbox and enable it.`);
      qc.invalidateQueries({ queryKey: SIGS_LIST_KEY });
    },
    onError: (err: unknown) => {
      const message =
        err && typeof err === "object" && "payload" in err
          ? ((err as { payload?: { error?: string } }).payload?.error ?? null)
          : null;
      toast.error(message ?? "Could not import this signature file.");
    },
  });

  const handleExport = async (sig: SignatureSummary) => {
    setExportingId(sig.id);
    try {
      const bundle = await signaturesApi.exportBundle(sig.id);
      const blob = new Blob([JSON.stringify(bundle, null, 2)], {
        type: "application/json",
      });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      const slug = sig.name.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "") || "signature";
      a.download = `${slug}.signature.json`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch {
      toast.error("Could not export this signature.");
    } finally {
      setExportingId(null);
    }
  };

  const handleImportFile = async (file: File) => {
    let bundle: SignatureBundle;
    try {
      bundle = JSON.parse(await file.text()) as SignatureBundle;
    } catch {
      toast.error("That file isn't a valid signature bundle.");
      return;
    }
    if (!bundle || bundle.kind !== "servicedesk.signature") {
      toast.error("That file isn't a signature bundle.");
      return;
    }
    importMut.mutate(bundle);
  };

  const findSetting = (key: string): SettingEntry | undefined =>
    settingsQ.data?.find((e) => e.key === key);

  return (
    <div className="flex flex-col gap-5">
      {/* Feature settings */}
      <section className="rounded-lg border border-glass-strong bg-glass p-5">
        <header className="mb-4 space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Feature settings
          </h2>
          <p className="text-xs text-muted-foreground">
            Master controls for the email signature feature.
          </p>
        </header>

        {settingsQ.isLoading ? (
          <Skeleton className="h-32 w-full" />
        ) : (
          <div>
            {findSetting(KEY_ENABLED) && (
              <SettingField
                entry={findSetting(KEY_ENABLED)!}
                queryKey={SIGS_SETTINGS_KEY}
                label="Enable signatures"
                hint="When off, no signature is appended to any outgoing mail."
              />
            )}
            {findSetting(KEY_APPEND_REPLIES) && (
              <SettingField
                entry={findSetting(KEY_APPEND_REPLIES)!}
                queryKey={SIGS_SETTINGS_KEY}
                label="Append on replies"
                hint="Place the agent's signature on reply mails too (directly under their message, above the quoted history)."
              />
            )}
            {findSetting(KEY_COMPOSER_PRELOAD) && (
              <SettingField
                entry={findSetting(KEY_COMPOSER_PRELOAD)!}
                queryKey={SIGS_SETTINGS_KEY}
                label="Pre-load in compose window"
                hint="Show the signature as a fixed, read-only block under your message while composing — directly above the quoted history — and send it from that position. When off, the signature is appended at the very bottom of the mail on send."
              />
            )}
            {findSetting(KEY_ENTRA_SYNC) && (
              <SettingField
                entry={findSetting(KEY_ENTRA_SYNC)!}
                queryKey={SIGS_SETTINGS_KEY}
                label="Entra ID sync"
                hint="Sync agent display name, job title, and phone from the connected Entra ID tenant. Requires Graph User.Read.All (Application) consent."
              />
            )}
            {findSetting(KEY_ENTRA_PHOTOS) && (
              <SettingField
                entry={findSetting(KEY_ENTRA_PHOTOS)!}
                queryKey={SIGS_SETTINGS_KEY}
                label="Sync profile photos from Entra"
                hint="Download profile photos from Entra ID and store them as signature assets. Entra sync must also be enabled. Requires Graph User.Read.All (Application) consent."
              />
            )}
            {findSetting(KEY_DEFAULT_SIG) && systemSigs.length > 0 && (
              <DefaultSignatureField
                entry={findSetting(KEY_DEFAULT_SIG)!}
                systemSigs={systemSigs}
              />
            )}
            {settingsQ.data && settingsQ.data.length === 0 && (
              <p className="text-xs text-muted-foreground/70">
                No Signatures settings are registered. Ensure SettingDefaults includes the Signatures category.
              </p>
            )}
          </div>
        )}
      </section>

      {/* My profile */}
      <MyProfileCard />

      {/* Team profiles */}
      <TeamProfilesCard />

      {/* Signatures list */}
      <section className="rounded-lg border border-glass-strong bg-glass p-5">
        <header className="mb-4 flex items-center justify-between gap-4">
          <div className="space-y-1">
            <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
              Signatures
            </h2>
            <p className="text-xs text-muted-foreground">
              Build block-tree signatures and assign them to mailboxes. System signatures are
              used by trigger/automated mail.
            </p>
          </div>
          <div className="flex items-center gap-2">
            <input
              ref={importInputRef}
              type="file"
              accept=".json,application/json"
              className="hidden"
              onChange={(e) => {
                const file = e.target.files?.[0];
                if (file) void handleImportFile(file);
                e.target.value = ""; // allow re-importing the same file
              }}
            />
            <Button
              size="sm"
              variant="ghost"
              onClick={() => importInputRef.current?.click()}
              disabled={importMut.isPending}
            >
              <Upload className="h-4 w-4" />
              {importMut.isPending ? "Importing…" : "Import"}
            </Button>
            <Button size="sm" onClick={onNew}>
              <Plus className="h-4 w-4" />
              New signature
            </Button>
          </div>
        </header>

        {listQ.isLoading && <Skeleton className="h-16 w-full" />}
        {listQ.isError && (
          <p className="text-sm text-red-300">Could not load signatures.</p>
        )}

        {!listQ.isLoading && sigsData.length === 0 && (
          <p className="text-sm text-muted-foreground">
            No signatures yet. Create one to start appending it to outgoing mail.
          </p>
        )}

        <ul className="flex flex-col gap-2">
          {sigsData.map((sig) => (
            <SignatureRow
              key={sig.id}
              sig={sig}
              exporting={exportingId === sig.id}
              onExport={() => handleExport(sig)}
              onEdit={() => onEdit(sig.id)}
              onDelete={() => setDeleteTarget(sig)}
            />
          ))}
        </ul>
      </section>

      {/* Delete confirm dialog */}
      <Dialog
        open={deleteTarget !== null}
        onOpenChange={(open) => {
          if (!open) setDeleteTarget(null);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete signature</DialogTitle>
            <DialogDescription>
              Delete &quot;{deleteTarget?.name}&quot;? This cannot be undone. Any queue currently
              using this signature will fall back to the default.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="ghost" onClick={() => setDeleteTarget(null)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              disabled={removeMut.isPending}
              onClick={() => deleteTarget && removeMut.mutate(deleteTarget.id)}
            >
              {removeMut.isPending ? "Deleting…" : "Delete"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

// ---- inline default-signature select ----

function DefaultSignatureField({
  entry,
  systemSigs,
}: {
  entry: SettingEntry;
  systemSigs: SignatureSummary[];
}) {
  const qc = useQueryClient();

  const save = useMutation({
    mutationFn: (value: string) => settingsApi.update(KEY_DEFAULT_SIG, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: SIGS_SETTINGS_KEY });
      toast.success("Default signature updated.");
    },
    onError: () => toast.error("Could not update setting."),
  });

  return (
    <div className="flex items-start justify-between gap-4 py-3 border-b border-glass last:border-b-0">
      <div className="min-w-0 flex-1 space-y-1">
        <p className="text-sm font-medium text-foreground">Default system signature</p>
        <p className="text-[10px] uppercase tracking-wider text-muted-foreground/40 font-mono">
          {KEY_DEFAULT_SIG}
        </p>
      </div>
      <div className="shrink-0 w-56">
        <Select
          value={entry.value || "__none"}
          onValueChange={(v) => save.mutate(v === "__none" ? "" : v)}
          disabled={save.isPending}
        >
          <SelectTrigger className="h-9 bg-glass text-sm">
            <SelectValue placeholder="None" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="__none">None</SelectItem>
            {systemSigs.map((s) => (
              <SelectItem key={s.id} value={s.id}>
                {s.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
    </div>
  );
}

// ---- my profile card ----

function MyProfileCard() {
  const qc = useQueryClient();

  const profileQ = useQuery({
    queryKey: PROFILE_KEY,
    queryFn: () => signatureProfileApi.get(),
  });

  const [displayName, setDisplayName] = useState("");
  const [jobTitle, setJobTitle] = useState("");
  const [workPhone, setWorkPhone] = useState("");
  const [mobilePhone, setMobilePhone] = useState("");
  const [dirty, setDirty] = useState(false);
  const [photoUploading, setPhotoUploading] = useState(false);

  // Populate from server
  const profile = profileQ.data;
  const [initialized, setInitialized] = useState(false);

  if (profile && !initialized) {
    setDisplayName(profile.displayName ?? "");
    setJobTitle(profile.jobTitle ?? "");
    setWorkPhone(profile.workPhone ?? "");
    setMobilePhone(profile.mobilePhone ?? "");
    setInitialized(true);
  }

  const saveMut = useMutation({
    mutationFn: () => {
      const body: MySignatureProfileUpdate = {
        displayName: displayName.trim() || null,
        jobTitle: jobTitle.trim() || null,
        workPhone: workPhone.trim() || null,
        mobilePhone: mobilePhone.trim() || null,
      };
      return signatureProfileApi.update(body);
    },
    onSuccess: () => {
      toast.success("Profile saved.");
      qc.invalidateQueries({ queryKey: PROFILE_KEY });
      qc.invalidateQueries({ queryKey: ["me", "signature-variables"] });
      setDirty(false);
    },
    onError: () => toast.error("Could not save profile."),
  });

  const deleteMut = useMutation({
    mutationFn: () => signatureProfileApi.deletePhoto(),
    onSuccess: () => {
      toast.success("Photo removed.");
      qc.invalidateQueries({ queryKey: PROFILE_KEY });
      qc.invalidateQueries({ queryKey: ["me", "signature-variables"] });
    },
    onError: () => toast.error("Could not remove photo."),
  });

  const handlePhotoUpload = async (file: File) => {
    setPhotoUploading(true);
    try {
      await signatureProfileApi.uploadPhoto(file);
      toast.success("Photo updated.");
      qc.invalidateQueries({ queryKey: PROFILE_KEY });
      qc.invalidateQueries({ queryKey: ["me", "signature-variables"] });
    } catch {
      toast.error("Photo upload failed.");
    } finally {
      setPhotoUploading(false);
    }
  };

  const handleChange = <T extends string>(setter: (v: T) => void) => (v: T) => {
    setter(v);
    setDirty(true);
  };

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          My signature profile
        </h2>
        <p className="text-xs text-muted-foreground">
          These fields feed the{" "}
          <code className="rounded bg-glass-strong px-1 py-0.5 font-mono text-[0.8em]">
            {"{{agent.*}}"}
          </code>{" "}
          variables in signature templates. Values entered here override what is synced from Entra ID.
        </p>
      </header>

      {profileQ.isLoading ? (
        <Skeleton className="h-28 w-full" />
      ) : (
        <div className="flex flex-col gap-5">
          {/* Photo */}
          <div className="flex items-center gap-4">
            <div className="relative h-16 w-16 shrink-0">
              {profile?.photoUrl ? (
                <img
                  src={profile.photoUrl}
                  alt="Profile photo"
                  className="h-16 w-16 rounded-full object-cover ring-2 ring-glass-strong"
                />
              ) : (
                <div className="flex h-16 w-16 items-center justify-center rounded-full bg-glass-strong text-muted-foreground/40">
                  <Camera className="h-6 w-6" />
                </div>
              )}
            </div>
            <div className="flex items-center gap-2">
              <label className={cn(
                "inline-flex cursor-pointer items-center gap-1.5 rounded border px-3 py-1.5 text-xs transition-colors",
                "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                photoUploading && "pointer-events-none opacity-50",
              )}>
                <Upload className="h-3.5 w-3.5" />
                {photoUploading ? "Uploading…" : profile?.hasPhoto ? "Replace photo" : "Upload photo"}
                <input
                  type="file"
                  accept="image/*"
                  className="hidden"
                  onChange={(e) => {
                    const file = e.target.files?.[0];
                    if (file) handlePhotoUpload(file);
                    e.target.value = "";
                  }}
                />
              </label>
              {profile?.hasPhoto && (
                <Button
                  size="sm"
                  variant="ghost"
                  disabled={deleteMut.isPending}
                  onClick={() => deleteMut.mutate()}
                >
                  <X className="h-3.5 w-3.5" />
                  Remove
                </Button>
              )}
            </div>
          </div>

          {/* Fields */}
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <div className="space-y-1.5">
              <label className="text-xs font-medium text-muted-foreground">Display name</label>
              <Input
                value={displayName}
                onChange={(e) => handleChange(setDisplayName)(e.target.value)}
                placeholder="Full name as shown in signature"
                className="h-9 bg-glass"
              />
            </div>
            <div className="space-y-1.5">
              <label className="text-xs font-medium text-muted-foreground">Job title</label>
              <Input
                value={jobTitle}
                onChange={(e) => handleChange(setJobTitle)(e.target.value)}
                placeholder="e.g. Support Engineer"
                className="h-9 bg-glass"
              />
            </div>
            <div className="space-y-1.5">
              <label className="text-xs font-medium text-muted-foreground">Work phone</label>
              <Input
                value={workPhone}
                onChange={(e) => handleChange(setWorkPhone)(e.target.value)}
                placeholder="+32 …"
                className="h-9 bg-glass"
                type="tel"
              />
            </div>
            <div className="space-y-1.5">
              <label className="text-xs font-medium text-muted-foreground">Mobile phone</label>
              <Input
                value={mobilePhone}
                onChange={(e) => handleChange(setMobilePhone)(e.target.value)}
                placeholder="+32 …"
                className="h-9 bg-glass"
                type="tel"
              />
            </div>
          </div>

          {dirty && (
            <div className="flex justify-end">
              <Button disabled={saveMut.isPending} onClick={() => saveMut.mutate()}>
                <Save className="h-4 w-4" />
                {saveMut.isPending ? "Saving…" : "Save profile"}
              </Button>
            </div>
          )}

          {profile?.entraSyncedUtc && (
            <p className="text-[11px] text-muted-foreground/50">
              Last synced from Entra: {new Date(profile.entraSyncedUtc).toLocaleString()}
            </p>
          )}
        </div>
      )}
    </section>
  );
}

// ---- signature row ----

function SignatureRow({
  sig,
  exporting,
  onExport,
  onEdit,
  onDelete,
}: {
  sig: SignatureSummary;
  exporting: boolean;
  onExport: () => void;
  onEdit: () => void;
  onDelete: () => void;
}) {
  return (
    <li
      className={cn(
        "flex items-center justify-between gap-4 rounded-lg border px-4 py-3",
        sig.enabled
          ? "border-glass-strong bg-glass"
          : "border-glass bg-glass opacity-60",
      )}
    >
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="truncate text-sm font-medium text-foreground">{sig.name}</span>
          {sig.isSystem && (
            <span className="inline-flex items-center rounded-full border border-violet-400/30 bg-violet-400/10 px-2 py-0.5 text-[10px] uppercase tracking-wider text-violet-200">
              System
            </span>
          )}
          {sig.enabled ? (
            <span className="inline-flex items-center gap-1 rounded-full border border-emerald-400/30 bg-emerald-400/10 px-2 py-0.5 text-[10px] uppercase tracking-wider text-emerald-300">
              <Check className="h-3 w-3" />
              Enabled
            </span>
          ) : (
            <span className="rounded-full bg-glass-strong px-2 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
              Disabled
            </span>
          )}
        </div>
        {sig.queueIds.length > 0 && (
          <p className="mt-1 text-[11px] text-muted-foreground/60">
            {sig.queueIds.length} mailbox{sig.queueIds.length !== 1 ? "es" : ""}
          </p>
        )}
      </div>
      <div className="flex items-center gap-2">
        <Button
          variant="ghost"
          size="sm"
          onClick={onExport}
          disabled={exporting}
          aria-label={`Export ${sig.name}`}
        >
          <Download className="h-4 w-4" />
          {exporting ? "Exporting…" : "Export"}
        </Button>
        <Button variant="ghost" size="sm" onClick={onEdit} aria-label={`Edit ${sig.name}`}>
          <Pencil className="h-4 w-4" />
          Edit
        </Button>
        <Button variant="ghost" size="sm" onClick={onDelete} aria-label={`Delete ${sig.name}`}>
          <Trash2 className="h-4 w-4" />
          Delete
        </Button>
      </div>
    </li>
  );
}

// ---- team profiles card ----

function TeamProfilesCard() {
  const qc = useQueryClient();
  const [frameVersion, setFrameVersion] = useState(0);
  const [hasFrame, setHasFrame] = useState(true);
  const [frameUploading, setFrameUploading] = useState(false);

  const teamQ = useQuery({
    queryKey: TEAM_PROFILES_KEY,
    queryFn: () => teamProfilesApi.list(),
  });

  const removeFrameMut = useMutation({
    mutationFn: () => photoFrameApi.remove(),
    onSuccess: () => {
      toast.success("Photo frame removed.");
      setHasFrame(false);
      setFrameVersion((v) => v + 1);
    },
    onError: () => toast.error("Could not remove frame."),
  });

  const handleFrameUpload = async (file: File) => {
    setFrameUploading(true);
    try {
      await photoFrameApi.upload(file);
      toast.success("Photo frame updated.");
      setHasFrame(true);
      setFrameVersion((v) => v + 1);
    } catch {
      toast.error("Frame upload failed.");
    } finally {
      setFrameUploading(false);
    }
  };

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          Team profiles
        </h2>
        <p className="text-xs text-muted-foreground">
          Set each agent&apos;s signature name, function, phone and photo. These fill the{" "}
          <code className="rounded bg-glass-strong px-1 py-0.5 font-mono text-[0.8em]">
            {"{{agent.*}}"}
          </code>{" "}
          variables for everyone — and override Entra ID.
        </p>
      </header>

      {/* Profile photo frame sub-section */}
      <div className="mb-5 rounded-lg border border-glass bg-glass-strong p-4">
        <div className="mb-2 space-y-0.5">
          <p className="text-xs font-medium text-foreground">Profile photo frame</p>
          <p className="text-[11px] text-muted-foreground">
            Optional background composited with each agent photo (e.g. a brand shape or cloud).
            Generic — upload your own. When set, each user&apos;s &quot;Edit photo&quot; button
            opens the canvas compositor.
          </p>
        </div>
        <div className="flex items-center gap-3">
          {hasFrame ? (
            <img
              src={`${FRAME_URL}?v=${frameVersion}`}
              alt="Photo frame"
              className="h-14 w-14 rounded border border-glass-strong object-contain"
              onError={() => setHasFrame(false)}
            />
          ) : (
            <div className="flex h-14 w-14 items-center justify-center rounded border border-glass-strong bg-glass text-muted-foreground/40">
              <ImageIcon className="h-5 w-5" />
            </div>
          )}
          <div className="flex items-center gap-2">
            <label
              className={cn(
                "inline-flex cursor-pointer items-center gap-1.5 rounded border px-3 py-1.5 text-xs transition-colors",
                "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                frameUploading && "pointer-events-none opacity-50",
              )}
            >
              <Upload className="h-3.5 w-3.5" />
              {frameUploading ? "Uploading…" : hasFrame ? "Replace frame" : "Upload frame"}
              <input
                type="file"
                accept="image/*"
                className="hidden"
                onChange={(e) => {
                  const file = e.target.files?.[0];
                  if (file) handleFrameUpload(file);
                  e.target.value = "";
                }}
              />
            </label>
            {hasFrame && (
              <Button
                size="sm"
                variant="ghost"
                disabled={removeFrameMut.isPending}
                onClick={() => removeFrameMut.mutate()}
                type="button"
              >
                <X className="h-3.5 w-3.5" />
                Remove
              </Button>
            )}
          </div>
        </div>
        {!hasFrame && (
          <p className="mt-2 text-[11px] text-muted-foreground/60">No frame set.</p>
        )}
      </div>

      {teamQ.isLoading && <Skeleton className="h-32 w-full" />}
      {teamQ.isError && (
        <p className="text-sm text-red-300">Could not load team profiles.</p>
      )}

      {!teamQ.isLoading && teamQ.data?.length === 0 && (
        <p className="text-sm text-muted-foreground">No agent or admin users found.</p>
      )}

      <ul className="flex flex-col gap-3">
        {(teamQ.data ?? []).map((profile) => (
          <TeamProfileUserRow
            key={profile.userId}
            profile={profile}
            frameVersion={frameVersion}
            hasFrame={hasFrame}
            onMutated={() => qc.invalidateQueries({ queryKey: TEAM_PROFILES_KEY })}
          />
        ))}
      </ul>
    </section>
  );
}

// ---- team profile user row ----

function TeamProfileUserRow({
  profile,
  frameVersion,
  hasFrame,
  onMutated,
}: {
  profile: TeamProfile;
  frameVersion: number;
  hasFrame: boolean;
  onMutated: () => void;
}) {
  const [displayName, setDisplayName] = useState(profile.displayName ?? "");
  const [jobTitle, setJobTitle] = useState(profile.jobTitle ?? "");
  const [workPhone, setWorkPhone] = useState(profile.workPhone ?? "");
  const [mobilePhone, setMobilePhone] = useState(profile.mobilePhone ?? "");
  const [dirty, setDirty] = useState(false);
  const [photoVersion, setPhotoVersion] = useState(0);
  const [photoDialogOpen, setPhotoDialogOpen] = useState(false);
  const [plainUploading, setPlainUploading] = useState(false);

  const saveMut = useMutation({
    mutationFn: () => {
      const body: TeamProfileUpdate = {
        displayName: displayName.trim() || null,
        jobTitle: jobTitle.trim() || null,
        workPhone: workPhone.trim() || null,
        mobilePhone: mobilePhone.trim() || null,
      };
      return teamProfilesApi.update(profile.userId, body);
    },
    onSuccess: () => {
      toast.success(`Profile saved for ${profile.email}.`);
      onMutated();
      setDirty(false);
    },
    onError: () => toast.error("Could not save profile."),
  });

  const deleteMut = useMutation({
    mutationFn: () => teamProfilesApi.deletePhoto(profile.userId),
    onSuccess: () => {
      toast.success("Photo removed.");
      setPhotoVersion((v) => v + 1);
      onMutated();
    },
    onError: () => toast.error("Could not remove photo."),
  });

  const handlePlainUpload = async (file: File) => {
    setPlainUploading(true);
    try {
      await teamProfilesApi.uploadPhoto(profile.userId, file);
      toast.success("Photo updated.");
      setPhotoVersion((v) => v + 1);
      onMutated();
    } catch {
      toast.error("Photo upload failed.");
    } finally {
      setPlainUploading(false);
    }
  };

  const handleChange =
    (setter: (v: string) => void) => (e: ChangeEvent<HTMLInputElement>) => {
      setter(e.target.value);
      setDirty(true);
    };

  const photoSrc = profile.photoUrl
    ? `${profile.photoUrl}?v=${photoVersion}`
    : null;

  const hasPhoto = profile.hasPhoto || photoVersion > 0;

  const handleCompositorSaved = () => {
    setPhotoDialogOpen(false);
    setPhotoVersion((v) => v + 1);
    onMutated();
  };

  return (
    <li className="rounded-lg border border-glass bg-glass p-4">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:gap-5">
        {/* Photo column */}
        <div className="flex shrink-0 flex-col items-center gap-2">
          <div className="relative h-14 w-14">
            {photoSrc ? (
              <img
                src={photoSrc}
                alt={`${profile.email} profile`}
                className="h-14 w-14 rounded-full object-cover ring-2 ring-glass-strong"
              />
            ) : (
              <div className="flex h-14 w-14 items-center justify-center rounded-full bg-glass-strong text-muted-foreground/40">
                <Camera className="h-5 w-5" />
              </div>
            )}
          </div>
          <Button
            size="sm"
            variant="ghost"
            type="button"
            className="h-auto gap-1 px-2 py-1 text-[11px]"
            onClick={() => setPhotoDialogOpen(true)}
          >
            <Pencil className="h-3 w-3" />
            Edit photo
          </Button>
          {hasPhoto && (
            <button
              type="button"
              disabled={deleteMut.isPending}
              onClick={() => deleteMut.mutate()}
              className={cn(
                "inline-flex items-center gap-1 rounded px-2 py-0.5 text-[11px] text-muted-foreground transition-colors hover:text-red-300",
                deleteMut.isPending && "pointer-events-none opacity-50",
              )}
            >
              <X className="h-3 w-3" />
              Remove
            </button>
          )}
        </div>

        {/* Fields column */}
        <div className="min-w-0 flex-1 space-y-3">
          {/* Identity row */}
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm font-medium text-foreground">{profile.email}</span>
            <span
              className={cn(
                "inline-flex items-center rounded-full px-2 py-0.5 text-[10px] uppercase tracking-wider",
                profile.role === "Admin"
                  ? "border border-violet-400/30 bg-violet-400/10 text-violet-200"
                  : "border border-sky-400/30 bg-sky-400/10 text-sky-200",
              )}
            >
              {profile.role}
            </span>
            {profile.entraSyncedUtc && (
              <span className="text-[10px] text-muted-foreground/50">
                Entra synced {new Date(profile.entraSyncedUtc).toLocaleDateString()}
              </span>
            )}
          </div>

          {/* Editable fields */}
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="space-y-1">
              <label className="text-[11px] font-medium text-muted-foreground">
                Display name
              </label>
              <Input
                value={displayName}
                onChange={handleChange(setDisplayName)}
                placeholder="Full name"
                className="h-8 bg-glass text-sm"
              />
            </div>
            <div className="space-y-1">
              <label className="text-[11px] font-medium text-muted-foreground">
                Job title
              </label>
              <Input
                value={jobTitle}
                onChange={handleChange(setJobTitle)}
                placeholder="e.g. Support Engineer"
                className="h-8 bg-glass text-sm"
              />
            </div>
            <div className="space-y-1">
              <label className="text-[11px] font-medium text-muted-foreground">
                Work phone
              </label>
              <Input
                value={workPhone}
                onChange={handleChange(setWorkPhone)}
                placeholder="+32 …"
                className="h-8 bg-glass text-sm"
                type="tel"
              />
            </div>
            <div className="space-y-1">
              <label className="text-[11px] font-medium text-muted-foreground">
                Mobile phone
              </label>
              <Input
                value={mobilePhone}
                onChange={handleChange(setMobilePhone)}
                placeholder="+32 …"
                className="h-8 bg-glass text-sm"
                type="tel"
              />
            </div>
          </div>

          {dirty && (
            <div className="flex justify-end">
              <Button size="sm" disabled={saveMut.isPending} onClick={() => saveMut.mutate()}>
                <Save className="h-3.5 w-3.5" />
                {saveMut.isPending ? "Saving…" : "Save"}
              </Button>
            </div>
          )}
        </div>
      </div>

      {/* Edit photo dialog */}
      <Dialog open={photoDialogOpen} onOpenChange={setPhotoDialogOpen}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle>Edit photo — {profile.email}</DialogTitle>
            <DialogDescription>
              {hasFrame
                ? "Drag the headshot to position it on the frame, adjust the scale, then save."
                : "Upload a photo for this user."}
            </DialogDescription>
          </DialogHeader>

          {hasFrame ? (
            <PhotoCompositor
              userId={profile.userId}
              frameVersion={frameVersion}
              onSaved={handleCompositorSaved}
            />
          ) : (
            <div className="flex flex-col gap-4 py-2">
              <label
                className={cn(
                  "inline-flex cursor-pointer items-center gap-1.5 self-start rounded border px-3 py-1.5 text-xs transition-colors",
                  "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                  plainUploading && "pointer-events-none opacity-50",
                )}
              >
                <Upload className="h-3.5 w-3.5" />
                {plainUploading ? "Uploading…" : hasPhoto ? "Replace photo" : "Upload photo"}
                <input
                  type="file"
                  accept="image/*"
                  className="hidden"
                  onChange={(e) => {
                    const file = e.target.files?.[0];
                    if (file) {
                      handlePlainUpload(file).then(() => setPhotoDialogOpen(false));
                    }
                    e.target.value = "";
                  }}
                />
              </label>
              <p className="text-[11px] text-muted-foreground/60">
                No photo frame is configured. Upload a frame above to enable the canvas compositor.
              </p>
            </div>
          )}

          {!hasFrame && (
            <DialogFooter>
              <Button variant="ghost" onClick={() => setPhotoDialogOpen(false)} type="button">
                Cancel
              </Button>
            </DialogFooter>
          )}
        </DialogContent>
      </Dialog>
    </li>
  );
}

// ---- editor panel wrapper ----

function EditorPanel({
  id,
  onDone,
}: {
  id: string | null;
  onDone: () => void;
}) {
  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-5 flex items-center justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            {id ? "Edit signature" : "New signature"}
          </h2>
          <p className="text-xs text-muted-foreground">
            Build the signature layout with rows and columns. Use the live preview to verify
            it looks right.
          </p>
        </div>
        <Button variant="ghost" size="sm" onClick={onDone}>
          <ArrowLeft className="h-4 w-4" />
          Back to list
        </Button>
      </header>
      <SignatureEditor initialId={id} onDone={onDone} />
    </section>
  );
}
