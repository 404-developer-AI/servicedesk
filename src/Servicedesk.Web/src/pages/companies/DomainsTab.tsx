import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Trash2 } from "lucide-react";
import { ApiError } from "@/lib/http";
import { companyApi } from "@/lib/companies-api";
import type { CompanyDomain } from "@/lib/ticket-api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useConfirm } from "@/components/ConfirmDialog";

/** The company detail page's Domains tab (admin only). */
export function DomainsTab({
  companyId,
  domains,
}: {
  companyId: string;
  domains: CompanyDomain[];
}) {
  const confirm = useConfirm();
  const qc = useQueryClient();
  const [value, setValue] = useState("");

  const add = useMutation({
    mutationFn: () => companyApi.addDomain(companyId, value.trim().toLowerCase()),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies", "detail", companyId] });
      setValue("");
      toast.success("Domain added");
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 409) {
        toast.error("This domain is already linked.");
      } else {
        toast.error("Add failed");
      }
    },
  });

  const remove = useMutation({
    mutationFn: (domainId: string) => companyApi.removeDomain(companyId, domainId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies", "detail", companyId] });
      toast.success("Domain removed");
    },
    onError: () => toast.error("Remove failed"),
  });

  return (
    <section className="glass-card space-y-3 p-5">
      <h2 className="text-sm font-medium text-foreground">Email domains</h2>
      <p className="text-xs text-muted-foreground">
        Incoming mail from these domains is automatically linked to this company
        (unless disabled in Settings → Mail).
      </p>
      <div className="flex gap-2">
        <Input
          value={value}
          onChange={(e) => setValue(e.target.value)}
          placeholder="acme.com"
          onKeyDown={(e) => {
            if (e.key === "Enter" && value.trim()) add.mutate();
          }}
        />
        <Button
          onClick={() => add.mutate()}
          disabled={!value.trim() || add.isPending}
        >
          Add
        </Button>
      </div>
      {domains.length === 0 ? (
        <p className="py-4 text-center text-sm text-muted-foreground">
          No domains yet.
        </p>
      ) : (
        <ul className="divide-y divide-glass">
          {domains.map((d) => (
            <li key={d.id} className="flex items-center justify-between py-2 text-sm">
              <span className="font-mono text-foreground">{d.domain}</span>
              <Button
                size="sm"
                variant="ghost"
                onClick={async () => {
                  if (
                    await confirm({
                      title: "Remove domain",
                      description: `Remove domain "${d.domain}"?`,
                      confirmLabel: "Remove",
                      tone: "destructive",
                    })
                  )
                    remove.mutate(d.id);
                }}
                disabled={remove.isPending}
              >
                <Trash2 className="h-3.5 w-3.5" />
              </Button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

