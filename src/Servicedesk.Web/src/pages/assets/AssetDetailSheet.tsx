import { useQuery } from "@tanstack/react-query";
import { ExternalLink, Server } from "lucide-react";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { assetsApi, trmmAdminApi } from "@/lib/api";
import { cn } from "@/lib/utils";

function formatWhen(iso: string | null): string {
  if (!iso) return "never";
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

type Props = {
  assetId: string | null;
  onClose: () => void;
};

export function AssetDetailSheet({ assetId, onClose }: Props) {
  const open = assetId !== null;
  const asset = useQuery({
    queryKey: ["assets", "detail", assetId] as const,
    queryFn: () => assetsApi.get(assetId!),
    enabled: open,
  });
  // Pulled lazily so the deep-link to the TRMM dashboard works even when
  // the user hasn't visited the integrations page in this session.
  const trmmStatus = useQuery({
    queryKey: ["integrations", "trmm", "status"] as const,
    queryFn: trmmAdminApi.status,
    enabled: open,
    staleTime: 60_000,
  });

  return (
    <Sheet open={open} onOpenChange={(o) => !o && onClose()}>
      <SheetContent className="w-full max-w-md sm:max-w-lg">
        <SheetHeader>
          <SheetTitle className="flex items-center gap-2">
            <Server className="h-4 w-4 text-primary" />
            {asset.data?.hostname ?? "Asset"}
          </SheetTitle>
        </SheetHeader>

        {asset.isLoading ? (
          <Skeleton className="mt-6 h-48 w-full" />
        ) : !asset.data ? (
          <p className="mt-6 text-sm text-muted-foreground">Asset not found.</p>
        ) : (
          <div className="mt-6 space-y-4 text-sm">
            <div className="flex items-center gap-2">
              <span
                className={cn(
                  "inline-block h-2 w-2 rounded-full",
                  asset.data.online ? "bg-emerald-400" : "bg-glass-strong",
                )}
              />
              <span className="text-foreground">
                {asset.data.online ? "Online" : "Offline"}
              </span>
              <Badge className="border border-glass bg-glass-strong text-[10px] font-normal capitalize">
                {asset.data.agentType}
              </Badge>
            </div>

            <DetailRow label="OS family" value={asset.data.osFamily ?? "—"} />
            <DetailRow label="OS" value={asset.data.osName ?? "—"} />
            <DetailRow label="Build" value={asset.data.osBuild ?? "—"} />
            <DetailRow
              label="EOL"
              value={
                asset.data.eolUtc
                  ? `${new Date(asset.data.eolUtc).toLocaleDateString()} (${asset.data.eolStatus})`
                  : "Unknown"
              }
            />
            <DetailRow label="Last seen" value={formatWhen(asset.data.lastSeenUtc)} />
            <DetailRow label="Public IP" value={asset.data.publicIp ?? "—"} />
            <DetailRow label="Client" value={asset.data.clientName} />
            {asset.data.companyName && (
              <DetailRow label="Linked company" value={asset.data.companyName} />
            )}
            <DetailRow label="Site" value={asset.data.siteName} />
            <DetailRow label="TRMM agent id" value={asset.data.trmmAgentId} mono />
            <DetailRow label="Last sync" value={formatWhen(asset.data.lastSyncUtc)} />

            {trmmStatus.data?.baseUrl && (
              <a
                href={`${trmmStatus.data.baseUrl}/agents/${asset.data.trmmAgentId}`}
                target="_blank"
                rel="noreferrer"
                className="mt-4 inline-flex items-center gap-1 text-xs text-primary hover:underline"
              >
                Open in Tactical RMM <ExternalLink className="h-3 w-3" />
              </a>
            )}
          </div>
        )}
      </SheetContent>
    </Sheet>
  );
}

function DetailRow({
  label,
  value,
  mono,
}: {
  label: string;
  value: string;
  mono?: boolean;
}) {
  return (
    <div className="flex items-baseline justify-between gap-4 border-b border-glass pb-2">
      <span className="text-xs text-muted-foreground">{label}</span>
      <span
        className={cn(
          "text-right text-sm text-foreground",
          mono && "font-mono text-xs",
        )}
      >
        {value}
      </span>
    </div>
  );
}
