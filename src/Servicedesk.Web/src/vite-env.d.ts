/// <reference types="vite/client" />

// Injected by the `define` block in vite.config.ts. "dev" outside the
// Docker build (no APP_VERSION env var). Guard usages with
// `typeof __APP_VERSION__ !== "undefined"` so vitest (which does not run
// vite.config.ts) works without it.
declare const __APP_VERSION__: string;
