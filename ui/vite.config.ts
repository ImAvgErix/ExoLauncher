import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'
import { vendorChunk } from './vite.chunks.ts'

const root = dirname(fileURLToPath(import.meta.url))

// Built assets land in ExoLauncher/wwwroot and are hosted via WebView2 virtual host.
// Vite 8 + Rolldown. React Compiler runs in Oxc (plugin-react v6), not Babel.
export default defineConfig({
  plugins: [
    react({
      compiler: { target: '19' },
    }),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': resolve(root, 'src'),
      src: resolve(root, 'src'),
    },
  },
  base: './',
  build: {
    manifest: true,
    outDir: resolve(root, '../ExoLauncher/wwwroot'),
    emptyOutDir: true,
    sourcemap: false,
    rolldownOptions: {
      input: {
        main: resolve(root, 'index.html'),
      },
      output: {
        manualChunks(id: string) {
          return vendorChunk(id)
        },
      },
    },
  },
  server: {
    port: 5174,
    strictPort: true,
  },
})
