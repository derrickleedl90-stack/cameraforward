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
      "/api": "http://localhost:3000",
      "/ws": {
        target: "ws://localhost:3000",
        ws: true
      }
    }
  }
});
