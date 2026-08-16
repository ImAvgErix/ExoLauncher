import { host } from './host'

export async function addPortableFolder(): Promise<{
  ok: boolean
  cancelled: boolean
  message: string | null
}> {
  const pick = await host.pickFolder('Choose game folder')
  if (!pick.ok || !pick.path) {
    return { ok: false, cancelled: true, message: pick.message ?? null }
  }
  const result = await host.install('local:add', pick.path, undefined)
  return {
    ok: result.ok,
    cancelled: false,
    message: result.message ?? (result.ok ? 'Portable game added.' : 'Could not add portable game'),
  }
}
