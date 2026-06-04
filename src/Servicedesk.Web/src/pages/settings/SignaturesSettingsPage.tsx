import { useState } from "react";
import {
  ArrowLeft,
  Camera,
  Check,
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
  type SignatureSummary,
  type MySignatureProfileUpdate,
} from "@/lib/signatures-api";
import { SignatureEditor } from "./signatures/SignatureEditor";

// ---- query keys ----

const SIGS_LIST_KEY = ["signatures", "list"] as const;
const SIGS_SETTINGS_KEY = ["settings", "list", "Signatures"] as const;
const PROFILE_KEY = ["me", "signature-profile"] as const;

// ---- setting keys ----

const KEY_ENABLED = "Signatures.Enabled";
const KEY_APPEND_REPLIES = "Signatures.AppendOnReplies";
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
                hint="Automatically append the agent's signature to reply mails."
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
          <Button size="sm" onClick={onNew}>
            <Plus className="h-4 w-4" />
            New signature
          </Button>
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
  onEdit,
  onDelete,
}: {
  sig: SignatureSummary;
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
