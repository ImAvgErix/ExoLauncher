import { createHash } from 'node:crypto'
import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs'
import { resolve, relative, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const output = resolve(root, '../ExoLauncher/wwwroot')
const stamp = resolve(root, 'node_modules/.cache/exo-web-build.json')
function files(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = resolve(directory, entry.name)
    return entry.isDirectory() ? files(path) : entry.isFile() ? [path] : []
  }).sort()
}
function digest(paths, base) {
  const hash = createHash('sha256')
  for (const path of paths) hash.update(relative(base, path)).update('\0').update(readFileSync(path)).update('\0')
  return hash.digest('hex')
}
const inputs = [
  ...['src', 'public', 'scripts'].flatMap(name => files(resolve(root, name))),
  ...readdirSync(root).filter(name => /^(package.*\.json|tsconfig.*\.json|vite.*\.ts|index\.html)$/.test(name)).map(name => resolve(root, name)),
  // Exo Mouse builds into wwwroot/mouse with the UI.
  ...files(resolve(root, '../mouse/src')),
  ...files(resolve(root, '../mouse/scripts')),
  ...['package.json', 'package-lock.json', 'tsconfig.json', 'vite.config.ts', 'index.html'].map(name => resolve(root, '../mouse', name)),
].sort()
const fingerprint = digest(inputs, root)
if (process.argv[2] === 'record') {
  mkdirSync(dirname(stamp), { recursive: true })
  writeFileSync(stamp, JSON.stringify({ fingerprint, output: digest(files(output), output) }))
} else {
  if (!existsSync(resolve(root, 'node_modules'))) throw new Error('Run npm ci in ui before building')
  let fresh = false
  try {
    const previous = JSON.parse(readFileSync(stamp, 'utf8'))
    fresh = previous.fingerprint === fingerprint && existsSync(resolve(output, 'index.html')) && previous.output === digest(files(output), output)
  } catch { /* missing or altered output needs a rebuild */ }
  if (fresh) console.log('Web UI inputs and generated assets unchanged; reusing build.')
  else {
    const result = spawnSync(process.platform === 'win32' ? 'npm.cmd' : 'npm', ['run', 'build'], {
      cwd: root, stdio: 'inherit', shell: process.platform === 'win32',
    })
    if (result.error) throw result.error
    process.exitCode = result.status ?? 1
  }
}
