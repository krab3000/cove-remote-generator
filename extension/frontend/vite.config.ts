import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Cove hosts React, TanStack Query and lucide for every extension. Source imports the bare packages
// (for types); the build rewrites them to Cove's runtime modules and leaves those external so the
// bundle joins the host's React tree instead of shipping its own copy.
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: [
      { find: /^react\/jsx-runtime$/, replacement: "@cove/runtime/react-jsx-runtime" },
      { find: /^react\/jsx-dev-runtime$/, replacement: "@cove/runtime/react-jsx-runtime" },
      { find: /^react$/, replacement: "@cove/runtime/react" },
      { find: /^@tanstack\/react-query$/, replacement: "@cove/runtime/react-query" },
      { find: /^lucide-react$/, replacement: "@cove/runtime/lucide-react" },
    ],
  },
  define: {
    "process.env.NODE_ENV": JSON.stringify("production"),
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    cssCodeSplit: false,
    lib: {
      entry: "src/index.tsx",
      formats: ["es"],
      fileName: () => "ui.mjs",
    },
    rollupOptions: {
      external: [/^@cove\/runtime\//],
    },
  },
});
