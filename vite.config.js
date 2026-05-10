import basicSsl from "@vitejs/plugin-basic-ssl";
import { defineConfig } from "vite";

export default defineConfig(({ command }) => ({
  base: "/",
  plugins: [
    basicSsl({
      name: "pawprints",
      domains: ["localhost", "127.0.0.1"],
    }),
  ],
  server: {
    https: true,
    proxy: {
      "/api": {
        target: process.env.VITE_API_PROXY_TARGET ?? "https://localhost:7233",
        changeOrigin: false,
        secure: false,
      },
    },
  },
}));
