import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const read = file => fs.readFileSync(path.join(root, file), 'utf8').replaceAll('\r\n', '\n')
const protocol = read('ExoLauncher/Services/ExoBridgeProtocol.cs')
const version = Number(protocol.match(/const int Version = (\d+)/)?.[1])
if (version !== 1) throw Error('Review the protocol version and generator before changing the bridge schema.')
// The wire records and their UI names. A property is a bool, a string, one of these records or an array of one.
const records = { ExoUpdateResponse: 'AppUpdateResponse', ExoWhatsNewRelease: 'WhatsNewWire', ExoWhatsNewItem: 'WhatsNewItemWire' }
const tsType = kind => {
  if (kind === 'bool') return 'boolean'
  if (kind === 'string') return 'string'
  if (kind.endsWith('[]')) return `${tsType(kind.slice(0, -2))}[]`
  if (records[kind]) return records[kind]
  throw Error(`Review the generator: the bridge schema has a type it does not know (${kind}).`)
}
const generated = '// Generated from ExoBridgeProtocol.cs. Run npm run contracts:write after reviewing wire changes.\n' + Object.entries(records).map(([record, name]) => {
  const body = protocol.match(new RegExp(`record ${record}\\s*\\{([\\s\\S]*?)\\n\\}`))?.[1]
  if (!body) throw Error(`Review the generator: the bridge schema has no record ${record}.`)
  const properties = [...body.matchAll(/public ([\w[\]]+)(\?)? (\w+) \{ get; init; \}/g)]
  return `export type ${name} = {\n${properties.map(([, kind, nullable, prop]) => `  ${prop[0].toLowerCase() + prop.slice(1)}${nullable ? '?' : ''}: ${tsType(kind)}`).join('\n')}\n}\n`
}).join('')
const uiFile = 'ui/src/lib/hostWire.ts'
if (process.argv.includes('--write')) fs.writeFileSync(path.join(root, uiFile), generated)
else if (read(uiFile) !== generated) throw Error('Generated UI wire types are stale. Review the native schema and run npm run contracts:write.')

const host = read('ui/src/lib/host.ts')
const tree = ts.createSourceFile('host.ts', host, ts.ScriptTarget.Latest, true)
const calls = new Map()
const events = new Map()
function visit(node) {
  if (ts.isCallExpression(node) && node.expression.getText(tree) === 'rawCall' && ts.isStringLiteral(node.arguments[0])) {
    calls.set(node.arguments[0].text, node.typeArguments?.[0]?.getText(tree).replace(/\s+/g, ' ') ?? 'unknown')
  }
  // Typed host events: the payload the UI relies on for each pushed name.
  if (ts.isInterfaceDeclaration(node) && node.name.text === 'HostEventMap') {
    for (const member of node.members) if (ts.isPropertySignature(member) && ts.isStringLiteral(member.name)) {
      events.set(member.name.text, member.type?.getText(tree).replace(/\s+/g, ' ') ?? 'unknown')
    }
  }
  ts.forEachChild(node, visit)
}
visit(tree)
const native = read('ExoLauncher/Services/WebHostBridge.cs')
for (const method of calls.keys()) if (!native.includes(`"${method}"`)) throw Error(`Missing native handler: ${method}`)
const nativeAll = fs.readdirSync(path.join(root, 'ExoLauncher'), { recursive: true })
  .filter(file => file.endsWith('.cs') && !/(^|[\\/])(bin|obj)[\\/]/.test(file))
  .map(file => read(path.join('ExoLauncher', file))).join('\n')
for (const event of events.keys()) if (!nativeAll.includes(`"${event}"`)) throw Error(`Missing native event: ${event}`)
const sorted = map => Object.fromEntries([...map].sort(([a], [b]) => a.localeCompare(b)))
const manifest = JSON.stringify({ version, methods: sorted(calls), events: sorted(events) }, null, 2) + '\n'
const manifestFile = path.join(root, 'ui/contracts/host-v1.json')
if (process.argv.includes('--write')) { fs.mkdirSync(path.dirname(manifestFile), { recursive: true }); fs.writeFileSync(manifestFile, manifest) }
else if (fs.readFileSync(manifestFile, 'utf8').replaceAll('\r\n', '\n') !== manifest) throw Error('Host/UI contract changed. Review consumers and regenerate the versioned manifest with npm run contracts:write.')
console.log(`Host protocol v${version}: ${calls.size} UI methods have native handlers, ${events.size} typed events have native emitters; generated update schema is current.`)
