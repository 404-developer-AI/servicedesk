import * as React from "react";
import { toast } from "sonner";
import { ApiError, ticketApi, type TicketAttachmentMeta } from "@/lib/ticket-api";

/// Lifecycle of a single in-flight or completed upload.
/// `pending` → upload is streaming; the chip shows a spinner.
/// `ready`   → server returned a TicketAttachmentMeta; chip is fully
///             interactive and the id is included in submit payloads.
/// `failed`  → upload errored; chip shows the message and a remove-only
///             affordance. We do NOT auto-retry — failures are usually
///             content-type or size-cap rejects that re-uploading wouldn't
///             fix without user action.
export type AttachmentUploadStatus = "pending" | "ready" | "failed";

export type AttachmentUploadItem = {
  /// Local id used for keys / removal before the server returns its uuid.
  /// Replaced once the server responds — but the localId stays for keying.
  localId: string;
  filename: string;
  size: number;
  status: AttachmentUploadStatus;
  /// Set once the upload succeeds. Submitting the post sends only these ids.
  meta?: TicketAttachmentMeta;
  errorMessage?: string;
};

/// Helper hook scoped to a single editor surface (a Note draft, a SendMail
/// composition, etc). Owns the in-flight uploads, exposes typed helpers for
/// the editor + tray, and returns a drainable list of attachment ids for the
/// submit payload. Call <c>reset()</c> after a successful submit so the
/// next post starts fresh.
export function useAttachmentUploads(ticketId: string) {
  const [items, setItems] = React.useState<AttachmentUploadItem[]>([]);

  const upload = React.useCallback(
    async (file: File): Promise<TicketAttachmentMeta | null> => {
      const localId =
        typeof crypto !== "undefined" && "randomUUID" in crypto
          ? crypto.randomUUID()
          : `local-${Date.now()}-${Math.random().toString(36).slice(2)}`;
      setItems((prev) => [
        ...prev,
        { localId, filename: file.name, size: file.size, status: "pending" },
      ]);
      try {
        const meta = await ticketApi.uploadAttachment(ticketId, file);
        setItems((prev) =>
          prev.map((it) =>
            it.localId === localId
              ? { ...it, status: "ready", meta }
              : it,
          ),
        );
        return meta;
      } catch (err) {
        const message =
          err instanceof ApiError
            ? err.message
            : err instanceof Error
              ? err.message
              : "Upload failed";
        setItems((prev) =>
          prev.map((it) =>
            it.localId === localId
              ? { ...it, status: "failed", errorMessage: message }
              : it,
          ),
        );
        toast.error(message);
        return null;
      }
    },
    [ticketId],
  );

  const remove = React.useCallback((localId: string) => {
    setItems((prev) => prev.filter((it) => it.localId !== localId));
  }, []);

  const reset = React.useCallback(() => {
    setItems([]);
  }, []);

  // Only rows that completed cleanly contribute to the submit payload.
  // Pending uploads block submit; failed ones are silently excluded but the
  // chip stays visible so the user can re-attach manually.
  const readyAttachmentIds = React.useMemo(
    () =>
      items
        .filter((it) => it.status === "ready" && it.meta)
        .map((it) => it.meta!.id),
    [items],
  );

  const hasPending = React.useMemo(
    () => items.some((it) => it.status === "pending"),
    [items],
  );

  return {
    items,
    upload,
    remove,
    reset,
    readyAttachmentIds,
    hasPending,
  };
}
