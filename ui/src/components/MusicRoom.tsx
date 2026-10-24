import { useEffect, useRef, useState } from 'react'
import { Button } from './ui/Button'
import { host, onHostEvent } from '../lib/host'
import type { MusicService } from '../lib/music'
import { report } from '../lib/report'
import { HostedRoom, type HostedState } from './HostedRoom'
import { useEmbeddedWebSurface } from './shell/useEmbeddedWebSurface'

type MusicStatus = {
  ok: boolean
  service?: MusicService
  ready?: boolean
  state?: HostedState
  message?: string
}
const services: Record<MusicService, { name: string; url?: string }> = {
  youtube: { name: 'YouTube Music', url: 'https://music.youtube.com/' },
  spotify: { name: 'Spotify' },
  apple: { name: 'Apple Music', url: 'https://music.apple.com/' },
}
/** Spotify is a desktop app; YouTube Music and Apple Music are their web
    players on the embedded web surface (shown here only to sign in). */
export function MusicRoom({
  active = true,
  service = 'spotify',
}: {
  active?: boolean
  service?: MusicService
}) {
  const serviceRef = useRef(service)
  const [attempt, setAttempt] = useState(0)
  const [status, setStatus] = useState<MusicStatus>({ ok: true, ready: false, service, state: 'loading' })
  const selected = services[service]
  const desktop = service === 'spotify'
  const current = status.service === service ? status : { ok: true, ready: false, state: 'loading' as const }
  const { surface, publishStatus } = useEmbeddedWebSurface<MusicStatus>({
    active, identity: service, attempt,
    send: async bounds => {
      const result = await host.musicSetBounds({ ...bounds, visible: true, service })
      return { ...result, service, state: result.state as HostedState | undefined }
    },
    hide: () => host.musicSetBounds({ visible: false }),
    onStatus: setStatus,
    failure: () => ({ ok: false, service, state: 'error', message: `${selected.name} could not load.` }),
  })

  useEffect(() => {
    serviceRef.current = service
  }, [service])

  useEffect(() => {
    return onHostEvent('music.status', (data) => {
      const result = data as MusicStatus
      if (result.service && result.service !== serviceRef.current) return
      publishStatus({ ...result, service: serviceRef.current })
    })
  }, [publishStatus])

  async function reload() {
    const requested = service
    publishStatus({ ok: true, ready: false, service, state: 'loading' })
    try {
      const result = await host.musicReload()
      if (serviceRef.current !== requested) return
      if (result.ok) setStatus({ ...result, service })
      else setAttempt((value) => value + 1)
    } catch (error) {
      report(error, 'MusicRoom.reload')
      if (serviceRef.current !== requested) return
      setAttempt((value) => value + 1)
    }
  }

  async function install() {
    publishStatus({ ok: true, service, state: 'installing' })
    try {
      const result = await host.musicInstall(service)
      if (!result.ok) publishStatus({ ok: false, service, state: 'error', message: result.message ?? `Could not install ${selected.name}. Try again.` })
    } catch (error) {
      report(error, 'MusicRoom.install')
      publishStatus({ ok: false, service, state: 'error', message: `Could not install ${selected.name}. Try again.` })
    }
  }

  async function openAgain() {
    const requested = service
    publishStatus({ ok: true, service, state: 'loading' })
    try {
      const result = await host.musicOpen(requested)
      if (!result.ok) publishStatus({ ok: false, service, state: 'error', message: result.message ?? `${selected.name} could not open.` })
    } catch (error) {
      report(error, 'MusicRoom.open')
      publishStatus({ ok: false, service, state: 'error', message: `${selected.name} could not open. Try again.` })
      setAttempt((value) => value + 1)
    }
  }

  async function openExternal() {
    if (!selected.url) return
    try {
      const result = await host.openUrl(selected.url)
      if (!result.ok) throw Error('Could not open the browser.')
    } catch (error) {
      report(error, 'MusicRoom.openExternal')
      setStatus({ ok: false, service, state: 'error', message: 'Could not open the browser. Try again.' })
    }
  }

  // A desktop provider's real state arrives as `music.status`; a layout reply
  // without one (the room measured mid-resize) is transient, not a failure.
  const state: HostedState = desktop
    ? (current.state ?? (current.ready ? 'running' : 'loading'))
    : (!current.ok ? 'error' : current.ready ? 'running' : 'loading')

  return (
    <HostedRoom
      name={selected.name}
      surface={surface}
      label={`${selected.name} player`}
      state={state}
      message={current.message}
      installLabel={`Install ${selected.name}`}
      retryLabel={desktop ? 'Open again' : 'Retry'}
      onInstall={() => void install()}
      onRetry={() => void (desktop ? openAgain() : reload())}
      extra={desktop ? null : (
        <Button size="sm" className="exo-set-btn" onPress={() => void openExternal()}>
          Open in browser
        </Button>
      )}
    />
  )
}
