import { useEffect, useState } from "react";

type SystemVersion = {
  version: string;
  commit: string;
  buildTime: string;
};

type SystemTime = {
  utc: string;
  timezone: string;
  offsetMinutes: number;
};

function formatServerLocal(time: SystemTime): string {
  const utcMs = new Date(time.utc).getTime();
  const shifted = new Date(utcMs + time.offsetMinutes * 60_000);
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${shifted.getUTCFullYear()}-${pad(shifted.getUTCMonth() + 1)}-${pad(shifted.getUTCDate())} ` +
    `${pad(shifted.getUTCHours())}:${pad(shifted.getUTCMinutes())}:${pad(shifted.getUTCSeconds())}`
  );
}

function App() {
  const [version, setVersion] = useState<SystemVersion | null>(null);
  const [time, setTime] = useState<SystemTime | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetch("/api/system/version")
      .then((r) => r.json())
      .then(setVersion)
      .catch((e) => setError(String(e)));
  }, []);

  useEffect(() => {
    let cancelled = false;
    const tick = () => {
      fetch("/api/system/time")
        .then((r) => r.json())
        .then((t) => {
          if (!cancelled) setTime(t);
        })
        .catch((e) => !cancelled && setError(String(e)));
    };
    tick();
    const id = window.setInterval(tick, 1000);
    return () => {
      cancelled = true;
      window.clearInterval(id);
    };
  }, []);

  return (
    <main className="min-h-screen flex items-center justify-center p-8">
      <div className="max-w-xl w-full rounded-2xl border border-white/10 bg-white/5 backdrop-blur-xl p-10 shadow-2xl">
        <h1 className="font-display text-5xl font-semibold mb-2 text-white">Servicedesk</h1>
        <p className="text-white/60 mb-8 text-sm tracking-wide uppercase">Foundations build — v0.0.1</p>

        {error && (
          <p className="text-red-400 text-sm">Failed to reach API: {error}</p>
        )}

        <dl className="space-y-3 text-sm">
          <div className="flex justify-between border-b border-white/10 pb-2">
            <dt className="text-white/50">Version</dt>
            <dd className="text-white font-mono">
              {version ? `${version.version} · ${version.commit}` : "…"}
            </dd>
          </div>
          <div className="flex justify-between border-b border-white/10 pb-2">
            <dt className="text-white/50">Server time</dt>
            <dd className="text-white font-mono">
              {time ? formatServerLocal(time) : "…"}
            </dd>
          </div>
          <div className="flex justify-between border-b border-white/10 pb-2">
            <dt className="text-white/50">Server time (UTC)</dt>
            <dd className="text-white font-mono">
              {time?.utc.replace("T", " ").slice(0, 19) ?? "…"}
            </dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-white/50">Server timezone</dt>
            <dd className="text-white font-mono">{time?.timezone ?? "…"}</dd>
          </div>
        </dl>
      </div>
    </main>
  );
}

export default App;
