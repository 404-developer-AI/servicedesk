export type SystemVersion = {
  version: string;
  commit: string;
  buildTime: string;
};

export type SystemTime = {
  utc: string;
  timezone: string;
  offsetMinutes: number;
};

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url, { headers: { Accept: "application/json" } });
  if (!res.ok) {
    throw new Error(`${url} → ${res.status} ${res.statusText}`);
  }
  return (await res.json()) as T;
}

export const systemApi = {
  version: () => getJson<SystemVersion>("/api/system/version"),
  time: () => getJson<SystemTime>("/api/system/time"),
};
