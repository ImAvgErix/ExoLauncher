import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { AppleMusic, Spotify, YouTube } from '../../brand/icons'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { host, onHostEvent } from '../../lib/host'
import { savedMusicService, saveMusicService, type MusicService } from '../../lib/music'
import { report } from '../../lib/report'
import type { Feedback } from './model'
import { Row, State } from '../settings/parts'

type AppState = 'loading' | 'installing' | 'notInstalled' | 'storeEdition' | 'installed' | 'running' | 'closed' | 'error'
type DesktopApp = { id: string; title: string; state: AppState }

const Discord = ({ size }: { size: number }) => <CompanyLogo company="discord" size={size} />

const SERVICES = [
  { id: 'spotify', label: 'Spotify', Icon: Spotify },
  { id: 'apple', label: 'Apple Music', Icon: AppleMusic },
  { id: 'youtube', label: 'YouTube Music', Icon: YouTube },
] as const

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

export function AppsStep({ onFeedback }: { onFeedback: (feedback: Feedback) => void }) {
  const [service, setService] = useState<MusicService>(savedMusicService)
  const [saving, setSaving] = useState(false)
  const [apps, setApps] = useState<DesktopApp[] | null>(null)
  const [installing, setInstalling] = useState<string | null>(null)
  const mounted = useRef(true)

  const refresh = useCallback(async () => {
    try {
      const result = await host.desktopClientsStatus()
      if (mounted.current && result.ok && result.apps)
        setApps(result.apps.map(app => ({ ...app, state: app.state as AppState })))
    } catch (error) {
      report(error, 'AppsStep.desktopClientsStatus')
      if (mounted.current) setApps([])
    }
  }, [])

  useEffect(() => {
    mounted.current = true
    void refresh()
    void host.webClientPreferences().then(
      next => { if (mounted.current && next.musicService) setService(next.musicService) },
      (error: unknown) => report(error, 'AppsStep.webClientPreferences'),
    )
    let timer = 0
    const soon = () => { window.clearTimeout(timer); timer = window.setTimeout(() => void refresh(), 500) }
    const dropFriends = onHostEvent('friends.status', soon)
    const dropMusic = onHostEvent('music.status', soon)
    return () => { mounted.current = false; window.clearTimeout(timer); dropFriends(); dropMusic() }
  }, [refresh])

  async function choose(next: MusicService) {
    if (next === service || saving) return
    const previous = service
    setService(next)
    setSaving(true)
    try {
      const saved = await host.setWebClientPreferences({ musicService: next })
      saveMusicService(saved.musicService ?? next)
    } catch {
      setService(previous)
      onFeedback({ message: 'Music service could not be saved. Try again.', error: true })
    } finally {
      setSaving(false)
    }
  }

  async function install(app: DesktopApp) {
    setInstalling(app.id)
    setApps(current => current?.map(item => item.id === app.id ? { ...item, state: 'installing' } : item) ?? null)
    try {
      const result = await host.desktopClientsInstall(app.id)
      if (!result.ok) onFeedback({ message: result.message ?? `Could not install ${app.title}. Try again.`, error: true })
    } catch (error) {
      report(error, 'AppsStep.desktopClientsInstall')
      onFeedback({ message: `Could not install ${app.title}. Try again.`, error: true })
    } finally {
      if (mounted.current) setInstalling(null)
      await refresh()
    }
  }

  const current = SERVICES.some(item => item.id === service) ? service : SERVICES[0].id
  function onServiceKey(event: KeyboardEvent<HTMLDivElement>) {
    const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0
    if (!step || saving) return
    event.preventDefault()
    const index = (SERVICES.findIndex(item => item.id === current) + step + SERVICES.length) % SERVICES.length
    event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="radio"]')[index]?.focus()
    void choose(SERVICES[index].id)
  }

  // The desktop app for the chosen service; YouTube Music and Apple Music play on the web, Discord is Exo's own.
  const relevant = (apps ?? []).filter(app => app.id === service)
  const web = SERVICES.find(item => item.id === service && (item.id === 'youtube' || item.id === 'apple'))

  return (
    <>
      {/* One tab stop, arrows move and choose (the radio pattern). */}
      <div className="exo-ob-services" role="radiogroup" aria-label="Music service" onKeyDown={onServiceKey}>
        {SERVICES.map(({ id, label, Icon }) => (
          <button
            key={id}
            type="button"
            role="radio"
            aria-checked={service === id}
            tabIndex={id === current ? 0 : -1}
            className={`exo-ob-service${service === id ? ' is-selected' : ''}`}
            // Not disabled while saving: a disabled tile would drop the focus the arrows just moved.
            aria-disabled={saving || undefined}
            onClick={() => void choose(id)}
          >
            <Icon size={28} />
            <span>{label}</span>
          </button>
        ))}
      </div>

      <div className="exo-set-list">
        {apps === null ? (
          <Row label="Checking apps…" />
        ) : relevant.length === 0 && web ? (
          <Row label={web.label} icon={<web.Icon size={22} />}><State on>Plays in Exo</State></Row>
        ) : null}
        {relevant.map(app => {
          const store = app.state === 'storeEdition'
          const canInstall = (app.state === 'notInstalled' || app.state === 'error' || store) && installing !== app.id
          const Icon = app.id === 'discord' ? Discord : SERVICES.find(item => item.id === app.id)?.Icon ?? Discord
          return (
            <Row key={app.id} label={app.title} icon={<Icon size={22} />}
              detail={store ? 'Can’t play inside Exo. Your account and library stay when you switch' : undefined}>
              <State on={app.state === 'installed' || app.state === 'running' || app.state === 'closed'}>{STATE_LABEL[app.state] ?? app.state}</State>
              {canInstall ? (
                <button type="button" className="exo-set-btn" onClick={() => void install(app)}>{store ? 'Switch to Spotify’s app' : `Install ${app.title}`}</button>
              ) : null}
            </Row>
          )
        })}
      </div>
    </>
  )
}
