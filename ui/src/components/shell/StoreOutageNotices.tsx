import { memo, useEffect } from 'react'
import { host, onHostEvent, type StoreId, type StoreOutage } from '../../lib/host'
import { notify } from '../../lib/notify'
import { report } from '../../lib/report'
import { confirm } from '../ui/Dialog'

/** What a restart into the store's own offline mode means, said before it happens. */
const OFFLINE_RESTART: Partial<Record<StoreId, string>> = {
  steam: 'Steam restarts in offline mode. Multiplayer and achievements sync once Steam is back.',
}

/** The outages already said (id and state): one notice each, though the host's list and its event both bring it. */
const said = new Set<string>()

/**
 * Asks (`title`, "Play Elden Ring offline?") before restarting a store in its own offline mode, then does it
 * (`restarting` once it starts). Null when the store has no offline restart or the user keeps it as it is, else the
 * host's answer.
 */
export async function playOffline(store: StoreId, title: string, restarting?: () => void): Promise<{ ok: boolean; message?: string | null } | null> {
  const body = OFFLINE_RESTART[store]
  if (!body || await confirm({ title, body, action: 'Play offline' }) !== true) return null
  restarting?.()
  try { return await host.storesPlayOffline(store) }
  catch (e) { return { ok: false, message: e instanceof Error ? e.message : 'Could not switch to offline mode.' } }
}

function show(outage: StoreOutage) {
  const key = `${outage.id}:${outage.state}`
  if (said.has(key)) return
  said.add(key)
  const id = `outage:${outage.store}`
  const offline = outage.state === 'down' && outage.canPlayOffline
  notify({
    id, title: outage.title, text: outage.text, tone: outage.state === 'back' ? 'good' : undefined, sticky: outage.state === 'down',
    actions: offline ? [{
      label: 'Play offline', primary: true,
      onPress: () => void playOffline(outage.store, 'Play offline?', () => notify({
        id, title: outage.title, text: 'Restarting in offline mode…', progress: null, lasting: true,
      })).then(answer => {
        if (!answer) return
        notify(answer.ok
          ? { id, title: outage.title, text: 'Offline mode is on. Exo switches back once the servers answer again.', tone: 'good' }
          : { id, title: outage.title, text: answer.message || 'Could not switch to offline mode.', tone: 'bad' })
      }),
    }] : undefined,
  })
}

/**
 * A store's servers down, said once per outage in Exo's one stack (DESIGN.md › Notify), with Play offline where the
 * store has its own offline mode to restart into; and once more when the servers are back. Asking for the list starts
 * the host watching.
 */
export const StoreOutageNotices = memo(function StoreOutageNotices() {
  useEffect(() => {
    const off = onHostEvent('stores.outage', show)
    host.storesOutages().then(({ outages }) => outages?.forEach(show)).catch((e: unknown) => report(e, 'StoreOutageNotices.list'))
    return off
  }, [])
  return null
})
