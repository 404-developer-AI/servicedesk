export type MailAttachment = {
  id: string;
  name: string;
  mimeType: string;
  size: number;
  url: string;
};

export function readAttachmentsFromMeta(meta: Record<string, unknown>): MailAttachment[] {
  if (!Array.isArray(meta.attachments)) return [];
  return (meta.attachments as Array<Partial<MailAttachment>>)
    .filter(
      (a): a is MailAttachment =>
        typeof a?.id === "string" &&
        typeof a?.name === "string" &&
        typeof a?.url === "string",
    )
    .map((a) => ({
      id: a.id,
      name: a.name,
      mimeType: typeof a.mimeType === "string" ? a.mimeType : "application/octet-stream",
      size: typeof a.size === "number" ? a.size : 0,
      url: a.url,
    }));
}
