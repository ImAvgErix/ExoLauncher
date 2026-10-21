import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'
import { existsSync } from 'node:fs'

const root = dirname(fileURLToPath(import.meta.url))
// Inside Exo Launcher's tree it builds into Exo's web root; on its own, into dist/.
const exoRoot = resolve(root, '../ExoLauncher/wwwroot')
const outDir = existsSync(exoRoot) ? resolve(exoRoot, 'mouse') : resolve(root, 'dist')

// Exo Mouse is its own program: it builds into its own folder beside Exo's UI
// and runs in a frame in the Devices room, talking to Exo only by messages.
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir,
    emptyOutDir: true,
    sourcemap: false,
    // Every brand's driver is one chunk (about 1 MB), loaded from disk after the page draws.
    chunkSizeWarningLimit: 1200,
  },
})
