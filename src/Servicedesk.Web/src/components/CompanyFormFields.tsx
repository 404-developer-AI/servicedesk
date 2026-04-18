import type { Dispatch, SetStateAction } from "react";
import { Bell } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import type { CompanyInput } from "@/lib/ticket-api";

/// Shared edit-form for companies. Used by the Overview-tab on
/// `/companies/:id` and by the `CompanyEditDialog` that opens from the
/// ticket-side-panel. Pure render + setter — the caller owns the form
/// state and the save mutation.
export function CompanyFormFields({
  form,
  setForm,
}: {
  form: CompanyInput;
  setForm: Dispatch<SetStateAction<CompanyInput>>;
}) {
  return (
    <div className="space-y-5">
      <div className="grid grid-cols-2 gap-3">
        <Field label="Customer code *" required>
          <Input
            value={form.code}
            onChange={(e) => setForm((f) => ({ ...f, code: e.target.value }))}
          />
        </Field>
        <Field label="Official name *" required>
          <Input
            value={form.name}
            onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
          />
        </Field>
        <Field label="Short name">
          <Input
            value={form.shortName ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, shortName: e.target.value }))}
          />
        </Field>
        <Field label="VAT number">
          <Input
            value={form.vatNumber ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, vatNumber: e.target.value }))}
          />
        </Field>
        <Field label="Website">
          <Input
            value={form.website ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, website: e.target.value }))}
          />
        </Field>
        <Field label="Phone">
          <Input
            value={form.phone ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, phone: e.target.value }))}
          />
        </Field>
        <Field label="Email">
          <Input
            type="email"
            value={form.email ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, email: e.target.value }))}
            placeholder="info@acme.be"
          />
        </Field>
      </div>

      <div className="rounded-md border border-amber-400/20 bg-amber-400/[0.03] p-4">
        <div className="mb-3 flex items-center gap-2 text-xs font-medium uppercase tracking-wide text-amber-300">
          <Bell className="h-3.5 w-3.5" /> Alert / note
        </div>
        <Field label="Alert text">
          <textarea
            value={form.alertText ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, alertText: e.target.value }))}
            rows={3}
            className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm outline-none focus:border-white/20"
          />
        </Field>
        <div className="mt-3 space-y-2">
          <label className="flex items-center justify-between gap-3 text-sm">
            <span>Pop-up when a ticket is created</span>
            <Switch
              checked={!!form.alertOnCreate}
              onCheckedChange={(v) => setForm((f) => ({ ...f, alertOnCreate: v }))}
            />
          </label>
          <label className="flex items-center justify-between gap-3 text-sm">
            <span>Pop-up when a ticket is opened</span>
            <Switch
              checked={!!form.alertOnOpen}
              onCheckedChange={(v) => setForm((f) => ({ ...f, alertOnOpen: v }))}
            />
          </label>
          {form.alertOnOpen && (
            <div className="mt-2 flex items-center gap-3 text-sm">
              <span className="text-muted-foreground">Frequency:</span>
              <label className="flex items-center gap-2">
                <input
                  type="radio"
                  name="alertMode"
                  checked={form.alertOnOpenMode === "session"}
                  onChange={() =>
                    setForm((f) => ({ ...f, alertOnOpenMode: "session" }))
                  }
                />
                Once per session
              </label>
              <label className="flex items-center gap-2">
                <input
                  type="radio"
                  name="alertMode"
                  checked={form.alertOnOpenMode === "every"}
                  onChange={() =>
                    setForm((f) => ({ ...f, alertOnOpenMode: "every" }))
                  }
                />
                Every time
              </label>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export function Field({
  label,
  required,
  children,
}: {
  label: string;
  required?: boolean;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {label}
        {required && <span className="ml-0.5 text-destructive">*</span>}
      </span>
      {children}
    </label>
  );
}

/// Helper to build the initial form state from a Company. Both callers —
/// Overview-tab and the Edit-dialog — need the same mapping, so centralise it.
export function companyToInput(company: {
  name: string;
  code: string;
  shortName: string;
  vatNumber: string;
  email: string;
  description: string;
  website: string;
  phone: string;
  addressLine1: string;
  addressLine2: string;
  city: string;
  postalCode: string;
  country: string;
  isActive: boolean;
  alertText: string;
  alertOnCreate: boolean;
  alertOnOpen: boolean;
  alertOnOpenMode: "session" | "every";
}): CompanyInput {
  return {
    name: company.name,
    code: company.code,
    shortName: company.shortName,
    vatNumber: company.vatNumber,
    email: company.email,
    description: company.description,
    website: company.website,
    phone: company.phone,
    addressLine1: company.addressLine1,
    addressLine2: company.addressLine2,
    city: company.city,
    postalCode: company.postalCode,
    country: company.country,
    isActive: company.isActive,
    alertText: company.alertText,
    alertOnCreate: company.alertOnCreate,
    alertOnOpen: company.alertOnOpen,
    alertOnOpenMode: company.alertOnOpenMode,
  };
}
