import { useMutation, useQueryClient } from "@tanstack/react-query";
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { CheckCircle2, Cpu, Info, Loader2, Play, ShieldCheck, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { apiErrorMessage } from "@/lib/api";
import { perfApi, type EdgeCheckItem, type PerfStatus, type PeriodQuery, type RuntimePoint } from "@/lib/perf-api";
import { cn } from "@/lib/utils";
import { useTabQuery } from "./usePerfQuery";
import {
  ChartTooltipBox,
  InfoTip,
  Legend,
  Panel,
  StatTile,
} from "./PerfUi";
import { axisTick, gridStroke, usePalette } from "./perfChart";
import { fmtBytes, fmtCount, fmtMs, fmtNum, fmtPct, fmtTime, type Tone } from "./perfFormat";

type SeriesDef = { key: keyof RuntimePoint; label: string; slot: number };

export function ServerTab({ period, status }: { period: PeriodQuery; status: PerfStatus | undefined }) {
  const q = useTabQuery(["host", period], () => perfApi.host(period));
  if (q.isLoading) return <Skeleton className="h-96 w-full" />;
  if (q.isError || !q.data) return <p className="text-sm text-muted-foreground">{apiErrorMessage(q.error) ?? "Could not load server metrics."}</p>;
  const d = q.data;
  const s = d.summary;
  const n = (k: string) => s[k] ?? null;
  const memPct = n("memTotalMb") && n("memAvailableMinMb") !== null ? (100 * (n("memAvailableMinMb") as number)) / (n("memTotalMb") as number) : null;
  const multiDay = new Date(period.to).getTime() - new Date(period.from).getTime() > 36 * 3_600_000;
  const series = d.series.map((p) => ({ ...p, ts: new Date(p.t).getTime() }));

  const tone = (bad: boolean, warn: boolean, has: boolean): Tone => (!has ? "nodata" : bad ? "critical" : warn ? "warning" : "good");

  return (
    <div className={cn("flex flex-col gap-5", q.isFetching && q.isPlaceholderData && "opacity-70")}>
      {!d.hostSupported ? (
        <div className="flex items-start gap-2 rounded-lg border border-glass bg-glass px-4 py-3 text-xs text-muted-foreground">
          <Info className="mt-0.5 h-4 w-4 shrink-0" />
          Host metrics (CPU steal, load, disk, swap) are read from Linux /proc and are not available on this machine ({d.os}).
          The .NET runtime metrics below are measured everywhere.
        </div>
      ) : null}

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <StatTile label="Host CPU" value={fmtPct(n("hostCpuPct"))} sub={`max ${fmtPct(n("hostCpuMax"))} · ${n("hostCores") ?? d.processorCount} cores`}
          tone={tone((n("hostCpuPct") ?? 0) > 85, (n("hostCpuMax") ?? 0) > 85, n("hostCpuPct") !== null)}
          info="CPU use of the whole server (all processes, PostgreSQL included), averaged over the period." />
        <StatTile label="CPU steal" value={fmtPct(n("stealPct"), 2)} sub={`max ${fmtPct(n("stealMax"), 1)}`}
          tone={tone((n("stealPct") ?? 0) > 10, (n("stealPct") ?? 0) > 5, n("stealPct") !== null)}
          info="Time the virtual server wanted the CPU but the hosting provider gave it to another customer ('noisy neighbour'). Above a few percent the VPS is oversold — a hosting problem, not a code problem." />
        <StatTile label="Free memory (lowest)" value={fmtPct(memPct)} sub={`${fmtNum(n("memAvailableMinMb"), 0)} MB of ${fmtNum(n("memTotalMb"), 0)} MB · swap ${fmtNum(n("swapUsedMaxMb"), 0)} MB`}
          tone={tone(memPct !== null && memPct < 10, memPct !== null && memPct < 20, memPct !== null)}
          info="Lowest available RAM in the period. When it runs out the server swaps to disk and everything slows down." />
        <StatTile label="Disk latency" value={fmtMs(n("diskAwaitMs"))} sub={`util avg ${fmtPct(n("diskUtilAvg"))} · max ${fmtPct(n("diskUtilMax"))}`}
          tone={tone((n("diskAwaitMs") ?? 0) > 50, (n("diskAwaitMs") ?? 0) > 20, n("diskAwaitMs") !== null)}
          info="Average time one disk read/write takes. SSD/NVMe should be well under 5 ms; tens of milliseconds slows every query that misses the cache." />
        <StatTile label="Disk space" value={fmtPct(n("rootFreePct"))} sub={`free on / · blobs ${fmtPct(n("blobFreePct"))} (${fmtNum(n("blobFreeGb"))} GB)`}
          tone={tone((n("rootFreePct") ?? 100) < 5, (n("rootFreePct") ?? 100) < 10, n("rootFreePct") !== null)}
          info="Lowest free space on the root filesystem and the attachment storage in the period. A full disk stops PostgreSQL." />
        <StatTile label="App CPU" value={fmtPct(n("cpuPct"))} sub={`max ${fmtPct(n("cpuPctMax"))} · GC pause ${fmtPct(n("gcPausePct"), 2)}`}
          info="CPU used by the Servicedesk process itself (share of the cores the container may use), and the share of time paused for garbage collection." />
        <StatTile label="Thread pool" value={fmtCount(n("tpThreadsMax"))} sub={`threads max · queue non-empty ${fmtPct(n("threadPoolQueuePct"))} of samples`}
          tone={tone(false, (n("threadPoolQueuePct") ?? 0) > 20, n("tpThreadsMax") !== null)}
          info="Worker threads of .NET. A growing thread count with work waiting in the queue means some code blocks a thread (.Result, .Wait(), synchronous I/O) — thread-pool starvation." />
        <StatTile label="DB connection pool" value={`${fmtCount(n("poolBusyMax"))} / ${fmtCount(n("poolMax"))}`} sub={`busy max · waits in ${fmtPct(n("poolWaitPct"))} of samples · ${fmtCount(n("poolTimeouts"))} timeouts`}
          tone={tone((n("poolTimeouts") ?? 0) > 0, (n("poolWaitPct") ?? 0) > 5, n("poolMax") !== null)}
          info="Database connections the app had open at most, against the pool's limit. When all are busy the next query waits — 'suddenly everything is slow'." />
      </div>

      <div className="grid gap-5 xl:grid-cols-2">
        <MetricChart title="CPU" info="Host CPU, steal and I/O wait (whole server) next to the app's own CPU — all in percent."
          data={series} multiDay={multiDay} unit="pct"
          defs={[{ key: "hostCpuPct", label: "Host", slot: 0 }, { key: "cpuPct", label: "App", slot: 2 }, { key: "stealPct", label: "Steal", slot: 1 }, { key: "ioWaitPct", label: "I/O wait", slot: 3 }]} />
        <MetricChart title="Load average" info="Processes running or waiting for CPU/disk (1-minute average). Compare with the core count: well above it means work queues up."
          data={series} multiDay={multiDay} unit="num" defs={[{ key: "load1", label: "Load (1 min)", slot: 0 }]} />
        <MetricChart title="Memory" info="Available RAM on the host and the app's memory (working set and managed heap), in MB."
          data={series} multiDay={multiDay} unit="mb"
          defs={[{ key: "memAvailableMb", label: "Host available", slot: 0 }, { key: "workingSetMb", label: "App working set", slot: 2 }, { key: "gcHeapMb", label: "Managed heap", slot: 6 }]} />
        <MetricChart title="Disk" info="Average I/O latency in milliseconds. Peaks that line up with slow requests point at the storage."
          data={series} multiDay={multiDay} unit="ms" defs={[{ key: "diskAwaitMs", label: "I/O latency", slot: 0 }]} />
        <MetricChart title="Thread pool" info="Thread count and the longest queue of waiting work items. Work should never queue for long."
          data={series} multiDay={multiDay} unit="num" defs={[{ key: "tpThreads", label: "Threads", slot: 0 }, { key: "tpQueue", label: "Queued items (max)", slot: 1 }]} />
        <MetricChart title="Database connection pool" info="Busy connections vs the pool size, and requests waiting for a free connection."
          data={series} multiDay={multiDay} unit="num"
          defs={[{ key: "poolBusy", label: "Busy", slot: 0 }, { key: "poolMax", label: "Pool size", slot: 6 }, { key: "poolPending", label: "Waiting", slot: 1 }]} />
        <MetricChart title="Garbage collection" info="Share of time the app was paused for garbage collection. More than a few percent hurts every request."
          data={series} multiDay={multiDay} unit="pct" defs={[{ key: "gcPausePct", label: "GC pause", slot: 0 }]} />
        <MetricChart title="Connections & concurrency" info="Open HTTP connections, realtime (SignalR) clients and requests handled at the same moment."
          data={series} multiDay={multiDay} unit="num"
          defs={[{ key: "kestrelActive", label: "HTTP connections", slot: 0 }, { key: "signalR", label: "SignalR clients", slot: 2 }, { key: "inFlight", label: "In-flight requests", slot: 1 }]} />
      </div>

      <div className="grid gap-5 xl:grid-cols-2">
        <BenchmarkPanel benchmarks={d.benchmarks} />
        <EdgeCheckPanel />
      </div>

      <p className="text-[11px] text-muted-foreground">
        {d.os} · .NET {d.dotnet} · {d.processorCount} cores visible to the app{status ? ` · version ${status.appVersion}` : ""}.
        Exceptions in period: {fmtCount(n("exceptions"))} · lock contentions: {fmtCount(n("lockContentions"))} · Gen 2 GCs: {fmtCount(n("gen2"))}.
      </p>
    </div>
  );
}

function MetricChart({
  title,
  info,
  data,
  defs,
  unit,
  multiDay,
}: {
  title: string;
  info: string;
  data: (RuntimePoint & { ts: number })[];
  defs: SeriesDef[];
  unit: "pct" | "ms" | "mb" | "num";
  multiDay: boolean;
}) {
  const pal = usePalette();
  const present = defs.filter((d) => data.some((p) => p[d.key] !== null && p[d.key] !== undefined));
  const fmt = (v: number | null | undefined) =>
    v === null || v === undefined ? "—" : unit === "pct" ? fmtPct(v) : unit === "ms" ? fmtMs(v) : unit === "mb" ? fmtBytes(v * 1024 * 1024) : fmtNum(v);

  return (
    <Panel title={title} info={info}
      actions={present.length > 1 ? <Legend items={present.map((d) => ({ label: d.label, color: pal.slot(d.slot) }))} /> : undefined}>
      {present.length === 0 ? (
        <p className="py-10 text-center text-xs text-muted-foreground">No data for this metric in the period.</p>
      ) : (
        <div className="h-44">
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={data} margin={{ top: 6, right: 8, left: -8, bottom: 0 }}>
              <CartesianGrid vertical={false} stroke={gridStroke} strokeOpacity={0.6} />
              <XAxis dataKey="ts" type="number" scale="time" domain={["dataMin", "dataMax"]} tick={axisTick} tickLine={false}
                axisLine={{ stroke: gridStroke }} tickFormatter={(v: number) => fmtTime(new Date(v).toISOString(), multiDay)} minTickGap={44} />
              <YAxis tick={axisTick} tickLine={false} axisLine={false} width={56} tickFormatter={(v: number) => fmt(v)}
                domain={unit === "pct" ? [0, (max: number) => Math.max(10, Math.ceil(max / 10) * 10)] : [0, "auto"]} />
              <Tooltip content={(props) => {
                const row = props.payload?.[0]?.payload as (RuntimePoint & { ts: number }) | undefined;
                if (!props.active || !row) return null;
                return <ChartTooltipBox title={new Date(row.ts).toLocaleString()} rows={present.map((d) => ({ label: d.label, value: fmt(row[d.key] as number | null), color: pal.slot(d.slot) }))} />;
              }} />
              {present.map((d) => (
                <Line key={String(d.key)} type="monotone" dataKey={d.key} stroke={pal.slot(d.slot)} strokeWidth={2} dot={false} connectNulls isAnimationActive={false} />
              ))}
            </LineChart>
          </ResponsiveContainer>
        </div>
      )}
    </Panel>
  );
}

function BenchmarkPanel({ benchmarks }: { benchmarks: { id: number; t: string; createdBy: string; result: Record<string, number | string | null> }[] }) {
  const qc = useQueryClient();
  const run = useMutation({
    mutationFn: () => perfApi.benchmark(),
    onSuccess: () => {
      toast.success("Benchmark finished");
      void qc.invalidateQueries({ queryKey: ["admin", "performance", "host"] });
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "Benchmark failed"),
  });
  const val = (r: Record<string, number | string | null>, k: string) => (typeof r[k] === "number" ? (r[k] as number) : null);
  return (
    <Panel
      title="Server benchmark"
      info="A short, explicit test (about 2 seconds): CPU hashing speed, disk write + fsync latency in the attachment storage, and PostgreSQL round-trip time. Run it on two servers — or before and after a hosting change — to compare like for like. It never runs on its own."
      actions={
        <Button size="sm" variant="outline" onClick={() => run.mutate()} disabled={run.isPending}>
          {run.isPending ? <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> : <Play className="mr-1.5 h-3.5 w-3.5" />}
          Run benchmark
        </Button>
      }
    >
      {benchmarks.length === 0 ? (
        <div className="flex items-center gap-2 text-xs text-muted-foreground"><Cpu className="h-4 w-4" /> No benchmark runs yet.</div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-xs">
            <thead>
              <tr className="text-left text-muted-foreground">
                <th className="py-1.5 pr-3 font-medium">When</th>
                <th className="py-1.5 pr-3 text-right font-medium">CPU (MB/s SHA-256)</th>
                <th className="py-1.5 pr-3 text-right font-medium">fsync</th>
                <th className="py-1.5 pr-3 text-right font-medium">Disk write</th>
                <th className="py-1.5 text-right font-medium">DB round trip</th>
              </tr>
            </thead>
            <tbody>
              {benchmarks.map((b) => (
                <tr key={b.id} className="border-t border-glass">
                  <td className="py-1.5 pr-3 text-muted-foreground">{new Date(b.t).toLocaleString()}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{fmtNum(val(b.result, "cpuSha256MbPerSec"), 0)}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{fmtMs(val(b.result, "diskFsyncMsAvg"))}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{val(b.result, "diskWriteMbPerSec") === null ? "—" : `${fmtNum(val(b.result, "diskWriteMbPerSec"))} MB/s`}</td>
                  <td className="py-1.5 text-right tabular-nums">{fmtMs(val(b.result, "pgRoundTripMsMedian"))}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  );
}

function EdgeCheckPanel() {
  const check = useMutation({ mutationFn: () => perfApi.edgeCheck() });
  const items: EdgeCheckItem[] = check.data?.items ?? [];
  return (
    <Panel
      title="Web server configuration"
      info="Fetches the public URL once from inside the app and reports what nginx returns: HTTP/2, compression, caching of the bundled JavaScript, keep-alive and HSTS. Wrong settings here cost load time on every visit."
      actions={
        <Button size="sm" variant="outline" onClick={() => check.mutate()} disabled={check.isPending}>
          {check.isPending ? <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> : <ShieldCheck className="mr-1.5 h-3.5 w-3.5" />}
          Check now
        </Button>
      }
    >
      {items.length === 0 ? (
        <p className="text-xs text-muted-foreground">{check.isError ? apiErrorMessage(check.error) ?? "Check failed." : "Not checked yet."}</p>
      ) : (
        <ul className="divide-y divide-glass text-xs">
          {items.map((i) => (
            <li key={i.label} className="flex items-start gap-2 py-2">
              {i.status === "good" ? <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-emerald-600 dark:text-emerald-300" />
                : i.status === "warning" ? <TriangleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-600 dark:text-amber-300" />
                : <Info className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" />}
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="font-medium text-foreground">{i.label}</span>
                  <span className="font-mono text-[11px] text-foreground/80">{i.value}</span>
                </div>
                <p className="mt-0.5 text-muted-foreground">{i.hint}</p>
              </div>
            </li>
          ))}
        </ul>
      )}
      <p className="mt-3 inline-flex items-center gap-1 text-[11px] text-muted-foreground">
        nginx also logs request and upstream timings per request
        <InfoTip text="The access log format includes $request_time and $upstream_response_time: the difference is time spent between nginx and the browser. Read it on the host with: docker compose logs nginx" />
      </p>
    </Panel>
  );
}
