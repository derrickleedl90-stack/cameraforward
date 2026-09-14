import { resolve } from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
  root: "web",
  build: {
    outDir: "../dist/web",
    emptyOutDir: true,
    rollupOptions: {
      input: {
        sender: resolve(import.meta.dirname, "web/index.html"),
        receiver: resolve(import.meta.dirname, "web/receiver.html")
      }
    }
  },
  server: {
    host: true,
    proxy: {
      // Use an explicit IPv4 loopback address and a dedicated development
      // port so localhost's IPv4/IPv6 listeners cannot reach different apps.
      "/api": "http://127.0.0.1:3100",
      "/ws": {
        target: "ws://127.0.0.1:3100",
        ws: true
      }
    }
  }
});
