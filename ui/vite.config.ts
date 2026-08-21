import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

const root = dirname(fileURLToPath(import.meta.url))

// Built assets land in ExoLauncher/wwwroot and are hosted via WebView2 virtual host.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': resolve(root, 'src'),
      src: resolve(root, 'src'),
    },
  },
  base: './',
  build: {
    outDir: resolve(root, '../ExoLauncher/wwwroot'),
    emptyOutDir: true,
    sourcemap: false,
    rollupOptions: {
      input: {
        main: resolve(root, 'index.html'),
        trophy: resolve(root, 'trophy.html'),
      },
      output: {
        manualChunks(id) {
          return id.includes('node_modules') ? 'vendor' : undefined
        },
      },
    },
  },
  server: {
    port: 5174,
    strictPort: true,
  },
})
