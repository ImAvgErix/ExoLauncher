import { host } from './host'

export async function addPortableFolder(): Promise<{
  ok: boolean
  cancelled: boolean
  message: string | null
}> {
  const pick = await host.pickFolder('Choose game folder')
  if (!pick.ok || !pick.path) {
    return { ok: false, cancelled: pick.cancelled === true, message: pick.message ?? 'The folder picker could not open.' }
  }
  const result = await host.install('local:add', pick.path, undefined)
  return {
    ok: result.ok,
    cancelled: false,
    message: result.message ?? (result.ok ? 'Game folder added.' : 'Could not add the game folder.'),
  }
}
