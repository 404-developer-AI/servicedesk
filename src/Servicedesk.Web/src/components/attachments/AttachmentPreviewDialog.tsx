import * as React from "react";
import { Download, ExternalLink } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

export type AttachmentPreview = {
  url: string;
  mimeType: string;
  filename: string;
  sizeLabel?: string;
  downloadUrl?: string;
};

type Props = {
  preview: AttachmentPreview | null;
  onOpenChange: (open: boolean) => void;
};

function getKind(mime: string): "image" | "pdf" | "text" | "other" {
  const m = mime.toLowerCase();
  if (m.startsWith("image/")) return "image";
  if (m === "application/pdf") return "pdf";
  if (
    m.startsWith("text/") ||
    m === "application/json" ||
    m === "application/xml" ||
    m === "application/x-log"
  )
    return "text";
  return "other";
}

export function canPreview(mime: string | undefined | null): boolean {
  return !!mime && getKind(mime) !== "other";
}

function ImagePreview({ url, alt }: { url: string; alt: string }) {
  const [native, setNative] = React.useState(false);
  return (
    <div
      className={cn(
        "flex max-h-[85vh] w-full overflow-auto rounded-md bg-black/40",
        native ? "items-start justify-start" : "items-center justify-center",
      )}
    >
      <img
        src={url}
        alt={alt}
        onClick={() => setNative((v) => !v)}
        className={cn(
          "select-none",
          native
            ? "max-w-none cursor-zoom-out"
            : "max-h-[85vh] max-w-full object-contain cursor-zoom-in",
        )}
      />
    </div>
  );
}

function PdfPreview({ url }: { url: string }) {
  return (
    <iframe
      src={url}
      title="PDF preview"
      className="h-[85vh] w-full rounded-md border border-white/10 bg-black/40"
    />
  );
}

function TextPreview({ url }: { url: string }) {
  const [state, setState] = React.useState<
    { kind: "loading" } | { kind: "ok"; text: string } | { kind: "error"; msg: string }
  >({ kind: "loading" });

  React.useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await fetch(url, { credentials: "include" });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const text = await res.text();
        if (!cancelled) setState({ kind: "ok", text });
      } catch (e) {
        if (!cancelled)
          setState({ kind: "error", msg: e instanceof Error ? e.message : "Load failed" });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [url]);

  if (state.kind === "loading") {
    return <Skeleton className="h-[60vh] w-full rounded-md" />;
  }
  if (state.kind === "error") {
    return (
      <p className="rounded-md border border-rose-400/30 bg-rose-500/10 p-4 text-sm text-rose-200">
        Could not load text preview: {state.msg}
      </p>
    );
  }
  return (
    <pre className="max-h-[75vh] overflow-auto whitespace-pre-wrap break-words rounded-md border border-white/10 bg-black/40 p-4 font-mono text-xs text-foreground/90">
      {state.text}
    </pre>
  );
}

export function AttachmentPreviewDialog({ preview, onOpenChange }: Props) {
  const kind = preview ? getKind(preview.mimeType) : "other";

  return (
    <Dialog open={preview !== null} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[95vh] w-[95vw] max-w-[1400px] overflow-hidden border-white/[0.06] bg-background/80 p-4 backdrop-blur-xl sm:rounded-lg">
        {preview ? (
          <>
            <DialogHeader className="pr-10">
              <DialogTitle className="truncate text-sm font-medium text-foreground">
                {preview.filename}
              </DialogTitle>
              <div className="flex items-center gap-3 text-xs text-muted-foreground">
                <span>{preview.mimeType}</span>
                {preview.sizeLabel ? <span>·</span> : null}
                {preview.sizeLabel ? <span>{preview.sizeLabel}</span> : null}
                <span className="ml-auto flex items-center gap-3">
                  <a
                    href={preview.url}
                    target="_blank"
                    rel="noreferrer"
                    className="inline-flex items-center gap-1 text-primary hover:underline"
                  >
                    <ExternalLink className="h-3 w-3" />
                    Open in new tab
                  </a>
                  <a
                    href={preview.downloadUrl ?? preview.url}
                    className="inline-flex items-center gap-1 text-primary hover:underline"
                  >
                    <Download className="h-3 w-3" />
                    Download
                  </a>
                </span>
              </div>
            </DialogHeader>
            <div className="mt-2">
              {kind === "image" ? (
                <ImagePreview url={preview.url} alt={preview.filename} />
              ) : kind === "pdf" ? (
                <PdfPreview url={preview.url} />
              ) : kind === "text" ? (
                <TextPreview url={preview.url} />
              ) : (
                <p className="rounded-md border border-white/10 bg-black/20 p-6 text-sm text-muted-foreground">
                  Preview is not supported for this file type.
                </p>
              )}
            </div>
          </>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}
