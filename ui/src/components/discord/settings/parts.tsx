import { useEffect, useState, type Dispatch, type ReactNode, type SetStateAction } from 'react'
import { host } from '../../../lib/host'
import { report } from '../../../lib/report'
import { changeAccountSettings, type AccountSettings, type DiscordClientSettings } from '../../../lib/discordPrefs'
import { Choice } from '../../settings/Choice'
import { Switch } from '../../Switch'
import { Slider } from '../../ui/Slider'

/** One of Discord's settings rows: the label and a short line on the left, the control on the right, an error under it. */
export function Row({ label, detail, error, children }: { label: string; detail?: ReactNode; error?: string | null; children?: ReactNode }) {
  return (
    <div className="exo-dc-settings-row">
      <span className="exo-dc-settings-copy"><strong>{label}</strong>{detail ? <span>{detail}</span> : null}{error ? <em role="alert">{error}</em> : null}</span>
      {children}
    </div>
  )
}

/** A heading over the rows that belong together, as Discord groups them (Account Info, Sounds, ...). */
export function Category({ title, detail, children }: { title?: string; detail?: string; children: ReactNode }) {
  return (
    <section className="exo-dc-settings-category">
      {title ? <h2>{title}</h2> : null}
      {detail ? <p>{detail}</p> : null}
      {children}
    </section>
  )
}

/** A switch row that saves itself; a failed save shows Discord's reason under it. */
export function ToggleRow({ label, detail, checked, disabled, onChange }: {
  label: string
  detail?: ReactNode
  checked: boolean
  disabled?: boolean
  onChange: (value: boolean) => Promise<string | null> | void
}) {
  const [error, setError] = useState<string | null>(null)
  return (
    <Row label={label} detail={detail} error={error}>
      <Switch checked={checked} disabled={disabled} label={label}
        onChange={value => { setError(null); void Promise.resolve(onChange(value)).then(e => setError(e ?? null)) }} />
    </Row>
  )
}

/** A switch on one of the account's settings; invert for Discord's "restricted" and "opted out" fields shown as allows. */
export function AccountToggle({ label, detail, settings, field, invert = false, disabled }: {
  label: string
  detail?: ReactNode
  settings: AccountSettings
  field: { [K in keyof AccountSettings]: AccountSettings[K] extends boolean ? K : never }[keyof AccountSettings]
  invert?: boolean
  disabled?: boolean
}) {
  return (
    <ToggleRow label={label} detail={detail} checked={settings[field] !== invert} disabled={disabled}
      onChange={value => changeAccountSettings({ [field]: value !== invert })} />
  )
}

/** A choice among a few, saved on change. */
export function ChoiceRow<T extends string | number>({ label, detail, value, choices, disabled, onChange }: {
  label: string
  detail?: ReactNode
  value: T
  choices: readonly { value: T; label: string }[]
  disabled?: boolean
  onChange: (value: T) => Promise<string | null> | void
}) {
  const [error, setError] = useState<string | null>(null)
  return (
    <Row label={label} detail={detail} error={error}>
      <Choice label={label} value={String(value)} disabled={disabled} choices={choices.map(c => ({ value: String(c.value), label: c.label }))}
        onChange={next => {
          const picked = choices.find(c => String(c.value) === next)
          if (!picked) return
          setError(null)
          void Promise.resolve(onChange(picked.value)).then(e => setError(e ?? null))
        }} />
    </Row>
  )
}

/** A slider that shows its value as it moves and saves when let go. */
export function SliderRow({ label, detail, min, max, step = 1, value, render, disabled, onCommit }: {
  label: string
  detail?: ReactNode
  min: number
  max: number
  step?: number
  value: number
  render: (value: number) => string
  disabled?: boolean
  onCommit: (value: number) => void
}) {
  const [moving, setMoving] = useState<number | null>(null)
  const shown = moving ?? value
  const commit = (next: number) => { setMoving(null); if (next !== value) onCommit(next) }
  return (
    <Row label={label} detail={detail}>
      <span className="exo-dc-settings-volume">
        <Slider min={min} max={max} step={step} value={shown} disabled={disabled} aria-label={label}
          onChange={event => setMoving(Number(event.target.value))}
          onPointerUp={event => commit(Number(event.currentTarget.value))} onKeyUp={event => commit(Number(event.currentTarget.value))} />
        <span>{render(shown)}</span>
      </span>
    </Row>
  )
}

/** A row whose action happens on Discord's own site (the password, two-factor, closing the account). */
export function LinkRow({ label, detail, button, url, danger }: { label: string; detail?: ReactNode; button: string; url: string; danger?: boolean }) {
  return (
    <Row label={label} detail={detail}>
      <button type="button" className={`exo-set-btn${danger ? ' is-danger' : ''}`} onClick={() => void host.openUrl(url).catch(() => { })}>{button}</button>
    </Row>
  )
}

/** Discord's settings page for the web, where what Exo's client can't do is done. */
export const DISCORD_SETTINGS_URL = 'https://discord.com/settings/account'

// What each settings page last read, by what it is: a section opens at once with it (they mount as you switch to
// them) and reads again behind it, never showing Loading for what it has shown before.
const lastRead = new Map<string, unknown>()

/** A page's state that starts from what was last read under key, and is kept there for the next time. */
export function useRemembered<T>(key: string): [T | null, Dispatch<SetStateAction<T | null>>] {
  const [value, setValue] = useState<T | null>(() => (lastRead.get(key) as T | undefined) ?? null)
  useEffect(() => { if (value !== null) lastRead.set(key, value) }, [key, value])
  return [value, setValue]
}

/**
 * The settings Exo keeps for its Discord client: null until read (error says why it wasn't, reload tries again); save
 * shows a change at once, then takes what was saved, or puts it back with why.
 */
export function useClientSettings() {
  const [settings, setSettings] = useRemembered<DiscordClientSettings>('client')
  const [error, setError] = useState<string | null>(null)
  const [round, setRound] = useState(0)
  useEffect(() => {
    let live = true
    host.discordClientSettings().then(read => { if (live) { setSettings(read); setError(null) } })
      .catch((err: unknown) => { report(err, 'DiscordSettings.client'); if (live) setError("Exo couldn't read these settings.") })
    return () => { live = false }
  }, [round, setSettings])
  const save = (change: Partial<DiscordClientSettings>) => {
    // Only this change's own settings go back if it fails, never another one's made meanwhile.
    const before = settings && Object.fromEntries(Object.keys(change).map(key => [key, settings[key as keyof DiscordClientSettings]])) as Partial<DiscordClientSettings>
    setSettings(s => s && { ...s, ...change })
    return host.discordClientSettings(change).then(saved => { setSettings(saved); return null }, (err: unknown) => {
      report(err, 'DiscordSettings.save')
      setSettings(s => s && before && { ...s, ...before })
      return "Exo didn't save that. Try again."
    })
  }
  return { settings, save, error, reload: () => { setError(null); setRound(r => r + 1) } }
}

/** A page's settings that didn't load: why, and Try Again. */
export function LoadError({ error, onRetry }: { error: string | null; onRetry: () => void }) {
  if (!error) return null
  return (
    <p className="exo-dc-settings-note" role="alert">
      {error} <button type="button" className="exo-set-btn" onClick={onRetry}>Try again</button>
    </p>
  )
}

/** A sound's Preview Sound link, as Discord puts one under each sound. */
export function Preview({ sound }: { sound: string }) {
  return <button type="button" className="exo-dc-settings-link" onClick={() => void host.discordSound(sound, true).catch(() => { })}>Preview sound</button>
}

/**
 * Loads something for a page (key: what it is); null until it first arrives, with the error line if it didn't. It
 * shows what was last read at once and reads again behind it; a failed read keeps what the page had.
 */
export function useLoad<T>(key: string, load: () => Promise<{ ok: boolean; message?: string | null; data?: unknown }>, map: (data: unknown) => T) {
  const [value, setValue] = useRemembered<T>(key)
  const [error, setError] = useState<string | null>(null)
  const [round, setRound] = useState(0)
  useEffect(() => {
    let live = true
    load().then(result => {
      if (!live) return
      if (result.ok) { setValue(map(result.data)); setError(null) }
      else setError(result.message ?? "Discord didn't answer. Try again.")
    }).catch(() => { if (live) setError("Discord didn't answer. Try again.") })
    return () => { live = false }
  }, [round]) // eslint-disable-line react-hooks/exhaustive-deps -- load and map are the page's own, fixed per page
  return { value, error, loading: value === null && error === null, reload: () => setRound(r => r + 1) }
}
