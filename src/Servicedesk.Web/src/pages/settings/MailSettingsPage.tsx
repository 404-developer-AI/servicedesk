import { useQuery } from "@tanstack/react-query";
import { Mail } from "lucide-react";
import { settingsApi, type SettingEntry } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";
import { GraphSection } from "./GraphSection";

const MAIL_QUERY_KEY = ["settings", "list", "Mail"] as const;
const STORAGE_QUERY_KEY = ["settings", "list", "Storage"] as const;
const JOBS_QUERY_KEY = ["settings", "list", "Jobs"] as const;

type Section = {
  title: string;
  description: string;
  keys: string[];
};

const SECTIONS: Section[] = [
  {
    title: "Polling & parsing",
    description: "How mailbox polling behaves and how mail bodies are normalized for search.",
    keys: [
      "Mail.PollingIntervalSeconds",
      "Mail.MaxBatchSize",
      "Mail.QuotedHistoryStripping",
    ],
  },
  {
    title: "Attachment limits",
    description: "Hard ceilings on incoming attachment and inline-image sizes.",
    keys: [
      "Storage.MaxAttachmentBytes",
      "Storage.InlineImageMaxBytes",
      "Storage.PerMailboxMonthlyCapMB",
    ],
  },
  {
    title: "Retention",
    description: "How long raw .eml copies and completed job records are kept.",
    keys: [
      "Storage.RawEmlRetentionDays",
      "Jobs.CompletedRetentionDays",
      "Jobs.DeadLetterAckedRetentionDays",
    ],
  },
  {
    title: "Disk monitoring",
    description: "Thresholds that raise disk-usage alerts and pause intake when storage is nearly full.",
    keys: [
      "Storage.BlobRoot",
      "Storage.BlobDiskWarnPercent",
      "Storage.BlobDiskCriticalPercent",
    ],
  },
];

const QUERY_KEYS: Record<string, readonly unknown[]> = {
  Mail: MAIL_QUERY_KEY,
  Storage: STORAGE_QUERY_KEY,
  Jobs: JOBS_QUERY_KEY,
};

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

export function MailSettingsPage() {
  const mail = useQuery({
    queryKey: MAIL_QUERY_KEY,
    queryFn: () => settingsApi.list("Mail"),
  });
  const storage = useQuery({
    queryKey: STORAGE_QUERY_KEY,
    queryFn: () => settingsApi.list("Storage"),
  });
  const jobs = useQuery({
    queryKey: JOBS_QUERY_KEY,
    queryFn: () => settingsApi.list("Jobs"),
  });

  const loading = mail.isLoading || storage.isLoading || jobs.isLoading;
  const byCategory: Record<string, SettingEntry[] | undefined> = {
    Mail: mail.data,
    Storage: storage.data,
    Jobs: jobs.data,
  };

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <Mail className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Mail</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Mailbox polling cadence, attachment limits and retention windows. Runtime behavior
            lands with v0.0.8 step 4 — for now these are the knobs the future pipeline will read.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      {loading ? (
        <div className="flex flex-col gap-3">
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-24 w-full" />
        </div>
      ) : (
        <>
        <GraphSection />
        {SECTIONS.map((section) => (
          <section
            key={section.title}
            className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5"
          >
            <header className="mb-4 space-y-1">
              <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
                {section.title}
              </h2>
              <p className="text-xs text-muted-foreground">{section.description}</p>
            </header>
            <div>
              {section.keys.map((key) => {
                const category = key.split(".")[0]!;
                const entry = findEntry(byCategory[category], key);
                if (!entry) return null;
                return (
                  <SettingField
                    key={key}
                    entry={entry}
                    queryKey={QUERY_KEYS[category]!}
                  />
                );
              })}
            </div>
          </section>
        ))}
        </>
      )}
    </div>
  );
}
