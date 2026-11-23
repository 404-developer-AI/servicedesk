import { request } from "@/lib/http";

// ---- Mail attachment diagnostics ----

export type MailAttachmentJobDiagnostic = {
  jobId: number;
  state: string;
  attemptCount: number;
  nextAttemptUtc: string;
  lastError?: string | null;
  updatedUtc: string;
  attachmentId: string;
};
export type MailAttachmentDiagnosticItem = {
  id: string;
  filename: string;
  mimeType: string;
  sizeBytes: number;
  isInline: boolean;
  contentId?: string | null;
  contentHash?: string | null;
  processingState: string;
  createdUtc: string;
  blobPresent: boolean;
  job?: MailAttachmentJobDiagnostic | null;
};
export type MailAttachmentDiagnostic = {
  mailMessageId: string;
  ticketId?: string | null;
  subject?: string | null;
  fromAddress?: string | null;
  receivedUtc: string;
  bodyHtmlBlobHash?: string | null;
  bodyHtmlBlobPresent: boolean;
  attachments: MailAttachmentDiagnosticItem[];
};
export type MailAttachmentSummary = {
  mailMessageId: string;
  ticketId?: string | null;
  subject?: string | null;
  fromAddress?: string | null;
  receivedUtc: string;
  attachmentTotal: number;
  readyCount: number;
  pendingCount: number;
  failedCount: number;
};
export const mailDiagnosticsApi = {
  list: (onlyIssues: boolean, limit = 25) =>
    request<MailAttachmentSummary[]>(
      "GET",
      `/api/admin/mail/diagnostics?limit=${limit}&onlyIssues=${onlyIssues ? "true" : "false"}`,
    ),
  get: (mailMessageId: string) =>
    request<MailAttachmentDiagnostic>("GET", `/api/admin/mail/diagnostics/${mailMessageId}`),
};
