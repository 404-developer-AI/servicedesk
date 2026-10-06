import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";
import { gzipSync } from "node:zlib";
import type { Plugin } from "vite";

// v0.1.24 — writes perf-bundle.json (chunk sizes, raw + gzip) next to
// index.html so Settings → Performance → Frontend can show the bundle
// budget. Chunk names and sizes only; the JavaScript itself is public anyway.
function perfBundleManifest(): Plugin {
  return {
    name: "sd-perf-bundle-manifest",
    apply: "build",
    generateBundle(_options, bundle) {
      const chunks: Array<{ name: string; type: string; bytes: number; gzipBytes: number; isEntry: boolean }> = [];
      for (const [fileName, output] of Object.entries(bundle)) {
        const isJs = fileName.endsWith(".js");
        const isCss = fileName.endsWith(".css");
        if (!isJs && !isCss) continue;
        const source = output.type === "chunk" ? output.code : output.source;
        const buffer = typeof source === "string" ? Buffer.from(source) : Buffer.from(source);
        chunks.push({
          name: fileName,
          type: isJs ? "js" : "css",
          bytes: buffer.length,
          gzipBytes: gzipSync(buffer).length,
          isEntry: output.type === "chunk" && output.isEntry,
        });
      }
      const js = chunks.filter((c) => c.type === "js");
      this.emitFile({
        type: "asset",
        fileName: "perf-bundle.json",
        source: JSON.stringify({
          builtUtc: new Date().toISOString(),
          totalJsBytes: js.reduce((n, c) => n + c.bytes, 0),
          totalJsGzipBytes: js.reduce((n, c) => n + c.gzipBytes, 0),
          totalCssBytes: chunks.filter((c) => c.type === "css").reduce((n, c) => n + c.bytes, 0),
          chunks: chunks.sort((a, b) => b.gzipBytes - a.gzipBytes),
        }),
      });
    },
  };
}

export default defineConfig({
  // The build version baked into the bundle (Docker passes APP_VERSION, the
  // same value the backend embeds via MinVerVersionOverride). "dev" outside
  // Docker: the update detection then anchors on the first fetched server
  // version instead, and no X-Client-Version header is sent.
  define: {
    __APP_VERSION__: JSON.stringify(process.env.APP_VERSION || "dev"),
  },
  plugins: [react(), tailwindcss(), perfBundleManifest()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: "http://localhost:5080",
        changeOrigin: true,
        secure: false,
      },
      "/hubs": {
        target: "http://localhost:5080",
        changeOrigin: true,
        secure: false,
        ws: true,
      },
    },
  },
});
