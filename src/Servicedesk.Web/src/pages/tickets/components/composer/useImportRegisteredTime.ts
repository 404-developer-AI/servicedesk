import * as React from "react";
import { toast } from "sonner";
import { timesheetTicketApi } from "@/lib/timesheet-api";
import type { ComposerEditorHandle } from "./composerTypes";

/// "Import registered time" button action on the Reply tab.
export function useImportRegisteredTime(
  ticketId: string,
  editorRef: React.MutableRefObject<ComposerEditorHandle | null>,
) {
  const [importing, setImporting] = React.useState(false);

  async function importRegisteredTime() {
    if (importing) return;
    setImporting(true);
    try {
      const { html } = await timesheetTicketApi.replyHtml(ticketId);
      if (!html.trim()) {
        toast.info("No time logged on this ticket yet");
        return;
      }
      const editor = editorRef.current;
      if (!editor) {
        toast.error("Editor not ready, try again");
        return;
      }
      editor.chain().focus().insertContent(html).run();
      toast.success("Time entries inserted");
    } catch {
      toast.error("Failed to load time entries");
    } finally {
      setImporting(false);
    }
  }

  return { importing, importRegisteredTime };
}
