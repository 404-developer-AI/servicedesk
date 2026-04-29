import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Link } from "@tanstack/react-router";
import { ExternalLink } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api";
import { companyApi, type Company, type CompanyInput } from "@/lib/ticket-api";
import { CompanyFormFields, companyToInput } from "@/components/CompanyFormFields";

type Props = {
  open: boolean;
  company: Company | null;
  onClose: () => void;
};

/// Full-form edit of a company from inside the ticket side-panel, without
/// leaving the ticket. Shares `CompanyFormFields` with the Overview-tab on
/// `/companies/:id` so the two forms never drift apart. Tabs-tabs content
/// like Contacts and Domains still lives on the detail page — the "Open
/// full page" link at the bottom covers that.
export function CompanyEditDialog({ open, company, onClose }: Props) {
  const qc = useQueryClient();
  const [form, setForm] = React.useState<CompanyInput | null>(null);

  // Rebuild the form each time we open the dialog with a (possibly) fresh
  // company, so sidepanel edits made between openings are reflected.
  React.useEffect(() => {
    if (open && company) setForm(companyToInput(company));
    if (!open) setForm(null);
  }, [open, company]);

  const save = useMutation({
    mutationFn: () => {
      if (!company || !form) throw new Error("No company to save");
      return companyApi.update(company.id, form);
    },
    onSuccess: () => {
      if (!company) return;
      qc.invalidateQueries({ queryKey: ["companies"] });
      qc.invalidateQueries({ queryKey: ["company", company.id] });
      toast.success("Company saved");
      onClose();
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 409) {
        toast.error("Another company already uses this code.");
      } else if (err instanceof ApiError && err.status === 403) {
        toast.error("You don't have permission to edit this company.");
      } else {
        toast.error("Save failed");
      }
    },
  });

  return (
    <Dialog open={open} onOpenChange={(v) => (!v ? onClose() : null)}>
      <DialogContent className="max-w-2xl max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>
            Edit company {company ? <span className="text-muted-foreground font-mono text-xs ml-1">{company.code}</span> : null}
          </DialogTitle>
          <DialogDescription>
            Changes apply to every ticket linked to this company.
          </DialogDescription>
        </DialogHeader>

        {form && (
          <CompanyFormFields
            form={form}
            setForm={setForm as React.Dispatch<React.SetStateAction<CompanyInput>>}
            adsolutLinked={Boolean(company?.adsolutId)}
          />
        )}

        <DialogFooter className="flex items-center justify-between gap-3 sm:justify-between">
          {company && (
            <Link
              to="/companies/$companyId"
              params={{ companyId: company.id }}
              className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
              onClick={onClose}
            >
              <ExternalLink className="h-3 w-3" />
              Open full page (contacts · domains)
            </Link>
          )}
          <div className="flex gap-2">
            <Button variant="ghost" onClick={onClose} disabled={save.isPending}>
              Cancel
            </Button>
            <Button
              onClick={() => save.mutate()}
              disabled={save.isPending || !form}
            >
              {save.isPending ? "Saving…" : "Save changes"}
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
