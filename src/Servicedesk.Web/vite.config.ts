import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "node:path";

export default defineConfig({
  // The build version baked into the bundle (Docker passes APP_VERSION, the
  // same value the backend embeds via MinVerVersionOverride). "dev" outside
  // Docker: the update detection then anchors on the first fetched server
  // version instead, and no X-Client-Version header is sent.
  define: {
    __APP_VERSION__: JSON.stringify(process.env.APP_VERSION || "dev"),
  },
  plugins: [react()],
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
