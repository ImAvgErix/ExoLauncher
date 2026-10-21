import { memo, useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { AppleMusic, Spotify, YouTube } from '../../brand/icons'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { host, onHostEvent, type WebClientOptions } from '../../lib/host'
import { savedMusicService, saveMusicService, type MusicService } from '../../lib/music'
import { report } from '../../lib/report'
import { Choice } from '../settings/Choice'
import { Row, Section, State } from './parts'

type AppState = 'loading' | 'installing' | 'notInstalled' | 'storeEdition' | 'installed' | 'running' | 'closed' | 'error'
type DesktopApp = { id: string; title: string; state: AppState }

const Discord = ({ size }: { size: number }) => <CompanyLogo company="discord" size={size} />

const SERVICES = [
  { value: 'spotify', label: 'Spotify' },
  { value: 'apple', label: 'Apple Music' },
  { value: 'youtube', label: 'YouTube Music' },
] as const

const ICONS: Record<string, (props: { size: number }) => ReactNode> = {
  discord: Discord,
  spotify: Spotify,
  apple: AppleMusic,
  youtube: YouTube,
}

const STATE_LABEL: Record<AppState, string> = {
  loading: 'Checking…',
  installing: 'Installing…',
  notInstalled: 'Not installed',
  storeEdition: 'Microsoft Store edition',
  installed: 'Installed',
  running: 'Running',
  closed: 'Installed',
  error: 'Not ready',
}

const PERMISSION_CHOICES = [
  { value: 'ask', label: 'Ask' },
  { value: 'allow', label: 'Allow' },
  { value: 'block', label: 'Block' },
] as const

let prepared: WebClientOptions | null = null
let pending: Promise<WebClientOptions> | null = null
function readPreferences(): Promise<WebClientOptions> {
  pending ??= host.webClientPreferences()
    .then(next => { prepared = next; return next })
    .finally(() => { pending = null })
  return pending
}

/** Last rows read, so every open after the first draws them at once and refreshes behind. */
let preparedApps: DesktopApp[] | null = null

async function readDesktopApps(): Promise<DesktopApp[] | null> {
  const result = await host.desktopClientsStatus()
  if (!result.ok || !result.apps) return null
  return preparedApps = result.apps.map(app => ({ ...app, state: app.state as AppState }))
}

/** Warmed after the library is usable, so the first open has its rows too. */
export function preloadAppsSection() {
  return Promise.all([readPreferences(), readDesktopApps()])
}

function useDesktopApps() {
  const [apps, setApps] = useState<DesktopApp[] | null>(preparedApps)
  const live = useRef(true)
  const refresh = useCallback(async () => {
    try {
      const next = await readDesktopApps()
      if (live.current && next) setApps(next)
    } catch (error) {
      report(error, 'AppsSection.desktopClientsStatus')
      if (live.current) setApps(current => current ?? [])
    }
  }, [])
  useEffect(() => {
    live.current = true
    void refresh()
    // Status arrives in bursts while an app starts; one read covers the burst.
    let timer = 0
    const soon = () => { window.clearTimeout(timer); timer = window.setTimeout(() => void refresh(), 500) }
    const dropMusic = onHostEvent('music.status', soon)
    return () => { live.current = false; window.clearTimeout(timer); dropMusic() }
  }, [refresh])
  return { apps, setApps, refresh }
}

function AppRow({ app, onInstalled }: { app: DesktopApp; onInstalled: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const Icon = ICONS[app.id] ?? Discord
  const store = app.state === 'storeEdition'
  const canInstall = !busy && (app.state === 'notInstalled' || app.state === 'error' || store)
  async function install() {
    setBusy(true)
    setError(null)
    try {
      const result = await host.desktopClientsInstall(app.id)
      if (!result.ok) setError(result.message ?? `Could not install ${app.title}. Try again.`)
    } catch (cause) {
      report(cause, 'AppsSection.desktopClientsInstall')
      setError(`Could not install ${app.title}. Try again.`)
    } finally {
      setBusy(false)
      await onInstalled()
    }
  }
  return (
    <Row label={app.title} icon={<Icon size={22} />} detail={store ? 'Can’t play inside Exo. Your account and library stay when you switch' : undefined} error={error}>
      <State on={app.state === 'running'}>{busy ? (store ? 'Switching…' : 'Installing…') : STATE_LABEL[app.state] ?? app.state}</State>
      {canInstall ? (
        <button type="button" className="exo-set-btn" onClick={() => void install()}>{store ? 'Switch to Spotify’s app' : `Install ${app.title}`}</button>
      ) : null}
    </Row>
  )
}

export const AppsSection = memo(function AppsSection() {
  const [options, setOptions] = useState<WebClientOptions | null>(prepared)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<{ row: string; text: string } | null>(null)
  const { apps, refresh } = useDesktopApps()
  const refreshAll = refresh

  useEffect(() => {
    let live = true
    void readPreferences().then(
      next => { if (live) setOptions(next) },
      () => { if (live) setError({ row: 'service', text: 'Music settings could not be loaded. Reopen Settings to retry.' }) },
    )
    return () => { live = false }
  }, [])

  /** `row` is the row whose control saved, so a failure reads under it. */
  async function save(patch: Partial<WebClientOptions>, row: 'service' | 'notifications') {
    setSaving(true)
    setError(null)
    try {
      const next = await host.setWebClientPreferences(patch)
      prepared = next
      setOptions(next)
      if (next.musicService) saveMusicService(next.musicService)
    } catch {
      setError({ row, text: 'The change could not be saved. Try again.' })
    } finally {
      setSaving(false)
    }
  }

  const service: MusicService = options?.musicService ?? savedMusicService()
  const music = (apps ?? []).filter(app => app.id === service)

  return (
    <Section id="apps" title="Music">
      <Row label="Music service" labelId="set-music-service" error={error?.row === 'service' ? error.text : null}>
        <Choice
          label="Music service"
          value={service}
          choices={SERVICES}
          disabled={saving || !options}
          onChange={value => void save({ musicService: value as MusicService }, 'service')}
        />
      </Row>
      {music.map(app => <AppRow key={app.id} app={app} onInstalled={refreshAll} />)}
      {service === 'youtube' ? (
        <Row label="YouTube Music" icon={<YouTube size={22} />}>
          <State on>Plays in Exo</State>
        </Row>
      ) : null}
      {service === 'youtube' ? (
        <Row label="YouTube Music notifications" labelId="set-yt-notifications" error={error?.row === 'notifications' ? error.text : null}>
          <Choice
            label="YouTube Music notifications"
            value={options?.notifications ?? 'ask'}
            choices={PERMISSION_CHOICES}
            disabled={saving || !options}
            onChange={value => void save({ notifications: value as WebClientOptions['notifications'] }, 'notifications')}
          />
        </Row>
      ) : null}
    </Section>
  )
})
