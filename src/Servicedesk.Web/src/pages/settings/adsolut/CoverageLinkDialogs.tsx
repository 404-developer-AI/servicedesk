import * as React from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { errorMessage } from "@/lib/http";
import {
  adsolutApi,
  type AdsolutCoverageCompanyRow,
  type AdsolutCoverageContactRow,
} from "@/lib/adsolut-api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

const isUuid = (v: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v.trim());


type LinkCompanyDialogProps = {
  open: boolean;
  row: AdsolutCoverageCompanyRow;
  onClose: () => void;
  onLinked: () => void;
};

export function LinkCompanyDialog({ open, row, onClose, onLinked }: LinkCompanyDialogProps) {
  const [adsolutId, setAdsolutId] = React.useState("");
  const [lastModified, setLastModified] = React.useState("");

  const link = useMutation({
    mutationFn: () =>
      adsolutApi.coverageLinkCompany(
        row.id,
        adsolutId.trim(),
        lastModified.trim() ? lastModified.trim() : null,
      ),
    onSuccess: () => {
      toast.success("Linked to Adsolut");
      onLinked();
    },
    onError: (err) => {
      toast.error(errorMessage(err, "Link failed"));
    },
  });

  const canSubmit = isUuid(adsolutId) && !link.isPending;

  return (
    <Dialog open={open} onOpenChange={(v) => !v && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Link {row.name} to Adsolut</DialogTitle>
          <DialogDescription>
            Paste the Adsolut customer UUID. Find it via the Lookup card on
            the Adsolut settings page (search by code or email). The
            lastModified timestamp is optional — leave blank to use server
            time; the next sync tick reconciles either way.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div className="space-y-1">
            <label className="text-xs text-muted-foreground">Adsolut customer UUID</label>
            <Input
              value={adsolutId}
              onChange={(e) => setAdsolutId(e.target.value)}
              placeholder="c04146fc-48ce-4f59-928a-1aad4d3bbbf9"
              className="font-mono text-[12px]"
            />
            {adsolutId.trim() && !isUuid(adsolutId) && (
              <p className="text-[11px] text-amber-300">Must be a UUID.</p>
            )}
          </div>
          <div className="space-y-1">
            <label className="text-xs text-muted-foreground">
              lastModified (ISO 8601, optional)
            </label>
            <Input
              value={lastModified}
              onChange={(e) => setLastModified(e.target.value)}
              placeholder="2026-05-05T12:34:56Z"
              className="font-mono text-[12px]"
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => link.mutate()} disabled={!canSubmit}>
            {link.isPending ? "Linking…" : "Link"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

type LinkContactDialogProps = {
  open: boolean;
  row: AdsolutCoverageContactRow;
  onClose: () => void;
  onLinked: () => void;
};

export function LinkContactDialog({ open, row, onClose, onLinked }: LinkContactDialogProps) {
  const [adsolutId, setAdsolutId] = React.useState("");
  const [lastModified, setLastModified] = React.useState("");

  const link = useMutation({
    mutationFn: () => {
      if (!row.linkId) throw new Error("missing_link_id");
      return adsolutApi.coverageLinkContact(
        row.linkId,
        adsolutId.trim(),
        lastModified.trim() ? lastModified.trim() : null,
      );
    },
    onSuccess: () => {
      toast.success("Linked contact to Adsolut");
      onLinked();
    },
    onError: (err) => {
      toast.error(errorMessage(err, "Link failed"));
    },
  });

  const canSubmit = isUuid(adsolutId) && !link.isPending;

  return (
    <Dialog open={open} onOpenChange={(v) => !v && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            Link {row.email} ({row.companyName ?? "no company"}) to Adsolut
          </DialogTitle>
          <DialogDescription>
            Paste the Adsolut customer-contact UUID for this work-relationship.
            One contact-link per Adsolut UUID — three customers under the
            same email = three different UUIDs.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div className="space-y-1">
            <label className="text-xs text-muted-foreground">
              Adsolut customer-contact UUID
            </label>
            <Input
              value={adsolutId}
              onChange={(e) => setAdsolutId(e.target.value)}
              placeholder="b3f1c0a4-6c13-4d4e-9f3a-7d2b0a1f7c5e"
              className="font-mono text-[12px]"
            />
            {adsolutId.trim() && !isUuid(adsolutId) && (
              <p className="text-[11px] text-amber-300">Must be a UUID.</p>
            )}
          </div>
          <div className="space-y-1">
            <label className="text-xs text-muted-foreground">
              lastModified (ISO 8601, optional)
            </label>
            <Input
              value={lastModified}
              onChange={(e) => setLastModified(e.target.value)}
              placeholder="2026-05-05T12:34:56Z"
              className="font-mono text-[12px]"
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => link.mutate()} disabled={!canSubmit}>
            {link.isPending ? "Linking…" : "Link"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
