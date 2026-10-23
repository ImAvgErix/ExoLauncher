import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Close, Loader2 } from '../../brand/icons'
import { host, type DlssRunResult, type DlssStatus, type Game, type GameProperties, type GamePropertyKey, type PropertyChoice } from '../../lib/host'
import { useDialogFocus } from '../../lib/focusTrap'
import { useDismiss } from '../../lib/dismiss'
import { useGlide } from '../../lib/glide'
import { invalidateUpscalerStatus } from '../../lib/upscalerCache'
import { gateReason, upscalerView } from '../../lib/upscalers'
import { formatSize, storeLabel } from '../../lib/utils'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { Switch } from '../Switch'
import { ArtworkTools, type Artwork } from './Artwork'

/**
 * A change that downloads, deletes or moves files (or lets Exo change an anti-cheat game's) waits in
 * its row for a second press; `also` is a second way to confirm it (Stop and restore).
 */
type Pending = {
  key: GamePropertyKey | 'verify' | 'restore' | 'consent'
  value: string
  text: string
  verb: string
  danger?: boolean
  also?: { verb: string; value: string; blocked?: string | null }
}

/** What every section needs: the properties, what is in flight, and the row helpers. */
type Rows = {
  props: GameProperties
  store: string
  /** The game is running: nothing that changes its files can start. */
  locked: boolean
  busy: string | null
  arm: (pending: Pending | null) => void
  apply: (key: GamePropertyKey, value?: string, secret?: string) => Promise<boolean>
  line: (row: string) => ReactNode
}

type SectionId = 'general' | 'files' | 'updates' | 'betas' | 'content' | 'artwork'

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : 'That change did not go through. Try again.'
}

/** What a change does to the disk, in one line: "Downloads 22.4 GB." */
function sizeLine(verb: 'Downloads' | 'Frees', bytes: number | null | undefined): string {
  if (bytes && bytes > 0) return `${verb} ${formatSize(bytes)}.`
  return verb === 'Downloads' ? 'Downloads what it needs.' : 'Its files are removed.'
}

function label(choices: PropertyChoice[], value: string): string {
  return choices.find(choice => choice.value === value)?.label ?? value
}

/** Which row an armed change belongs to: one row per DLC, Verify's own row. */
function rowOf(pending: Pending): string {
  return pending.key === 'content' ? `content:${pending.value.slice(0, pending.value.lastIndexOf(':'))}` : pending.key
}

function Spinner({ on }: { on: boolean }) {
  return on ? <Loader2 size={14} className="animate-spin motion-reduce:animate-none" /> : null
}

/** The sections this game's store has, in Steam's order, with Artwork (Exo's own) last. */
function sectionsFor(props: GameProperties): { id: SectionId; title: string }[] {
  return [
    // General only with something in it (a Riot game has none of these).
    ...(props.launchOptions !== null || props.language !== null || props.cloud !== null || props.cloudSync || props.overlay !== null || props.storeProperties
      ? [{ id: 'general' as const, title: 'General' }] : []),
    { id: 'files' as const, title: 'Installed files' },
    ...(props.autoUpdate !== null || props.background !== null ? [{ id: 'updates' as const, title: 'Updates' }] : []),
    ...(props.beta !== null && props.betaChoices.length > 0 ? [{ id: 'betas' as const, title: 'Betas' }] : []),
    ...(props.content && props.content.length > 0 ? [{ id: 'content' as const, title: props.contentLabel }] : []),
    { id: 'artwork' as const, title: 'Artwork' },
  ]
}

/**
 * An installed game's Properties over its page: a wide floating panel with a
 * section list on the left (as Settings) and one section beside it, so nothing
 * scrolls. What the game's store offers (Steam: launch options, language, cloud
 * saves, the overlay, files, updates, betas, DLC; Epic: optional content and
 * cloud saves), only the rows that store has, and Exo's own: the folder, the
 * upscalers' original files and the cover. A row that downloads, deletes or
 * moves files says how much and asks once more in place; the store's own
 * download then shows in Exo's downloads.
 */
export function PropertiesSheet({ game, artwork, upscalers: upscalerStatus, onUpscalersChanged, onClose, onStatus }: {
  game: Game
  artwork: Artwork
  upscalers: DlssStatus | null
  onUpscalersChanged: () => void
  onClose: () => void
  onStatus: (message: string | null, sticky?: boolean) => void
}) {
  const dialog = useRef<HTMLDivElement>(null)
  useDialogFocus(dialog)
  useDismiss(dialog, onClose, { modal: true })
  const navGlide = useGlide<HTMLUListElement>()
  const [section, setSection] = useState<SectionId>('general')
  const [props, setProps] = useState<GameProperties | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)
  const [notes, setNotes] = useState<Record<string, { text: string; bad?: boolean }>>({})

  const load = useCallback(() => host.properties(game.id).then((result) => {
    if (result.ok) setProps(result)
    else setLoadError(result.message ?? 'Exo could not read this game\'s properties.')
  }).catch((error: unknown) => setLoadError(errorText(error))), [game.id])

  useEffect(() => { void load() }, [load])

  // While Steam moves the game, its progress is read again every two seconds.
  const moving = !!props?.moving
  useEffect(() => {
    if (!moving) return
    const timer = window.setInterval(() => void load(), 2000)
    return () => window.clearInterval(timer)
  }, [moving, load])

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      // Escape closes this panel, not the game page under it; an armed row stands down first.
      event.preventDefault()
      event.stopPropagation()
      if (pending) setPending(null)
      else onClose()
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [pending, onClose])

  const note = (row: string, text: string, bad = false) => setNotes(current => ({ ...current, [row]: { text, bad } }))

  const apply = async (key: GamePropertyKey, value = '', secret?: string, row: string = key) => {
    setPending(null)
    setBusy(row)
    note(row, '')
    try {
      const result = await host.setProperty(game.id, key, value, secret)
      if (result.properties?.ok) setProps(result.properties)
      note(row, result.message ?? (result.ok ? '' : 'That change did not go through.'), !result.ok)
      if (result.ok && result.message) onStatus(result.message)
      return result.ok
    } catch (error) {
      note(row, errorText(error), true)
      return false
    } finally {
      setBusy(null)
    }
  }

  /** An upscaler change (the originals back, or the anti-cheat consent), then the game page reads the files again. */
  const upscalers = (row: 'restore' | 'consent', change: () => Promise<DlssRunResult>, done: string) => {
    setPending(null)
    setBusy(row)
    note(row, '')
    void change()
      .then((result) => {
        invalidateUpscalerStatus(game.id)
        onUpscalersChanged()
        const failed = result.files?.find(file => file.state === 'failed')
        note(row, failed?.message ?? (result.ok ? done : result.message ?? 'That change did not go through.'), !result.ok || !!failed)
      })
      .catch((error: unknown) => note(row, errorText(error), true))
      .finally(() => setBusy(null))
  }

  const confirm = (armed: Pending, value = armed.value) => {
    if (armed.key === 'restore') {
      upscalers('restore', () => host.dlssRestore(game.id), 'The game\'s own files are back.')
      return
    }
    if (armed.key === 'consent') {
      // Consent changes no file; Stop and restore also puts the game's own files back.
      const allow = value === 'allow'
      upscalers('consent', async () => {
        const answer = await host.dlssConsent(game.id, allow, upscalerView(upscalerStatus).antiCheatName ?? undefined)
        if (!answer.ok || value !== 'restore') return { ok: answer.ok, message: answer.message }
        return host.dlssRestore(game.id)
      }, allow ? 'Exo may update this game\'s upscalers.' : value === 'restore' ? 'Stopped. The game\'s own files are back.' : 'Stopped.')
      return
    }
    if (armed.key !== 'verify') {
      void apply(armed.key, armed.value, undefined, rowOf(armed))
      return
    }
    setPending(null)
    note('verify', 'Checking files. Progress shows in Downloads.')
    void host.repair(game.id)
      .then(result => onStatus(result.message ?? (result.ok ? 'Files verified.' : 'Verify failed.'), !result.ok))
      .catch((error: unknown) => onStatus(errorText(error), true))
  }

  /** The row's own line: an armed change waiting for its second press, or what the last change said. */
  const line = (row: string): ReactNode => {
    if (pending && rowOf(pending) === row) {
      return (
        <div className="exo-props-confirm" role="group" aria-label="Confirm">
          <span>{pending.text}</span>
          <button type="button" className="exo-set-btn" onClick={() => setPending(null)}>Cancel</button>
          <button type="button" className={`exo-set-btn${pending.danger ? ' is-danger' : ' is-primary'}`} data-autofocus onClick={() => confirm(pending)}>
            {pending.verb}
          </button>
          {pending.also && (
            <button
              type="button"
              className="exo-set-btn"
              disabled={!!pending.also.blocked}
              aria-label={pending.also.blocked ? `${pending.also.verb}: ${pending.also.blocked}` : undefined}
              onClick={() => confirm(pending, pending.also?.value)}
            >
              {pending.also.verb}
            </button>
          )}
        </div>
      )
    }
    const said = notes[row]
    if (!said?.text) return null
    return <p className={said.bad ? 'exo-set-error' : 'exo-props-note'} role={said.bad ? 'alert' : 'status'}>{said.text}</p>
  }

  const rows: Rows | null = props && {
    props,
    store: storeLabel(props.store),
    locked: props.running,
    busy,
    arm: setPending,
    apply: (key, value, secret) => apply(key, value, secret),
    line,
  }
  const sections = props ? sectionsFor(props) : [{ id: 'general' as const, title: 'General' }]
  const current = sections.find(entry => entry.id === section) ?? sections[0]

  const pick = (id: SectionId) => {
    setPending(null)
    setSection(id)
  }

  return createPortal(
    <div className="exo-props-back">
      <div ref={dialog} className="exo-props" role="dialog" aria-modal="true" aria-labelledby="exo-props-title" tabIndex={-1}>
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        <nav className="exo-settings-nav exo-props-nav" aria-label="Properties sections">
          <h1>Properties</h1>
          <ul ref={navGlide} className="exo-glide">
            {sections.map(entry => (
              <li key={entry.id}>
                <button
                  type="button"
                  aria-current={current.id === entry.id ? 'true' : undefined}
                  className={current.id === entry.id ? 'is-current' : undefined}
                  onClick={() => pick(entry.id)}
                >
                  {entry.title}
                </button>
              </li>
            ))}
          </ul>
        </nav>
        <div className="exo-props-pane">
          <header className="exo-props-head">
            <div className="exo-props-title">
              <h2 id="exo-props-title">{current.title}</h2>
              <span>{game.title}</span>
            </div>
            {props?.running ? <State>Playing</State> : null}
            <button type="button" className="exo-props-close" aria-label="Close" onClick={onClose}><Close size={16} /></button>
          </header>
          {rows ? (
            <div key={current.id} className="exo-props-section exo-set-list exo-motion-switch">
              {current.id === 'general' && <GeneralSection {...rows} />}
              {current.id === 'files' && <FilesSection {...rows} gameId={game.id} game={game} upscalers={upscalerStatus} />}
              {current.id === 'updates' && <UpdatesSection {...rows} />}
              {current.id === 'betas' && <BetasSection {...rows} />}
              {current.id === 'content' && <ContentSection {...rows} pending={pending} />}
              {current.id === 'artwork' && (
                <PropRow label="Cover" detail={artwork.custom ? 'Your own picture.' : 'The store\'s art.'} line={null}>
                  <ArtworkTools artwork={artwork} disabled={false} className="exo-set-btn" />
                </PropRow>
              )}
            </div>
          ) : (
            <p className={loadError ? 'exo-set-error' : 'exo-props-note'}>{loadError ?? 'Reading properties…'}</p>
          )}
        </div>
      </div>
    </div>,
    document.body,
  )
}

function PropRow({ label: name, detail, line, children }: { label: string; detail?: string; line: ReactNode; children: ReactNode }) {
  return (
    <div className="exo-props-row">
      <Row label={name} detail={detail}>{children}</Row>
      {line}
    </div>
  )
}

function GeneralSection({ props, locked, busy, arm, apply, line }: Rows) {
  const languages = props.language !== null && props.languageChoices.length > 1 ? props.languageChoices : null
  return (
    <>
      {props.launchOptions !== null && (
        <LaunchOptions value={props.launchOptions} busy={busy === 'launchOptions'} onSave={value => apply('launchOptions', value)} line={line('launchOptions')} />
      )}
      {languages && (
        <PropRow label="Language" line={line('language')}>
          <Spinner on={busy === 'language'} />
          <Choice
            label="Language"
            value={props.language ?? ''}
            choices={languages}
            disabled={locked || busy !== null}
            onChange={value => arm({
              key: 'language', value, verb: 'Switch',
              text: `${label(languages, value)}. ${sizeLine('Downloads', languages.find(choice => choice.value === value)?.sizeBytes)}`,
            })}
          />
        </PropRow>
      )}
      {props.cloud !== null && (
        <PropRow label="Cloud saves" line={line('cloud')}>
          <Spinner on={busy === 'cloud'} />
          <Switch checked={props.cloud} label="Cloud saves" disabled={busy !== null} onChange={on => void apply('cloud', on ? 'on' : 'off')} />
        </PropRow>
      )}
      {props.cloudSync && (
        <PropRow label="Cloud saves" line={line('cloudSync')}>
          <button type="button" className="exo-set-btn" disabled={busy !== null || locked} onClick={() => void apply('cloudSync')}>
            <Spinner on={busy === 'cloudSync'} />Sync now
          </button>
        </PropRow>
      )}
      {props.overlay !== null && (
        <PropRow label="Steam overlay" detail="Off saves frames. Steam closes for a moment to change it." line={line('overlay')}>
          <Spinner on={busy === 'overlay'} />
          <Switch checked={props.overlay} label="Steam overlay" disabled={busy !== null} onChange={on => void apply('overlay', on ? 'on' : 'off')} />
        </PropRow>
      )}
      {props.storeProperties && (
        <PropRow label={`Controller in ${props.storeProperties}`} line={line('storeProperties')}>
          <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void apply('storeProperties')}>
            <Spinner on={busy === 'storeProperties'} />Open {props.storeProperties}
          </button>
        </PropRow>
      )}
    </>
  )
}

function FilesSection({ props, locked, busy, arm, line, gameId, game, upscalers }: Rows & { gameId: string; game: Game; upscalers: DlssStatus | null }) {
  const size = props.sizeBytes ? formatSize(props.sizeBytes) : null
  const folders = props.folder !== null && props.folderChoices.length > 1 ? props.folderChoices : null
  const view = upscalerView(upscalers)
  const gate = gateReason({ gameRunning: !!game.canStop })
  return (
    <>
      <PropRow label="Size on disk" line={null}>
        <State>{size ?? 'Unknown'}</State>
        {props.canBrowse && <button type="button" className="exo-set-btn" onClick={() => void host.openFolder(gameId)}>Open folder</button>}
      </PropRow>
      {props.verifyLabel && (
        <PropRow label="Damaged or missing files" line={line('verify')}>
          <button
            type="button"
            className="exo-set-btn"
            disabled={locked || busy !== null}
            onClick={() => arm({
              key: 'verify', value: '', verb: props.verifyLabel ?? 'Verify',
              text: `Checks ${size ? `all ${size}` : 'every file'} and downloads any damaged files.`,
            })}
          >
            {props.verifyLabel}
          </button>
        </PropRow>
      )}
      {folders && (
        <PropRow label="Library folder" line={line('folder')}>
          {props.moving ? <State>Moving {Math.floor(props.moving.done / Math.max(1, props.moving.total) * 100)}%</State> : (
            <Choice
              label="Library folder"
              value={props.folder ?? ''}
              choices={folders.map(choice => ({ value: choice.value, label: choice.detail ? `${choice.label} · ${choice.detail}` : choice.label }))}
              disabled={locked || busy !== null}
              onChange={value => arm({ key: 'folder', value, verb: 'Move', text: `Moves ${size ?? 'the game'} to ${label(folders, value)}.` })}
            />
          )}
        </PropRow>
      )}
      {view.antiCheatName && (view.consent === 'available' || view.consent === 'given') && (
        <PropRow label="Upscaler changes" detail={`This game uses ${view.antiCheatName}.`} line={line('consent')}>
          <Spinner on={busy === 'consent'} />
          <State>{view.consent === 'given' ? 'Allowed' : 'Not allowed'}</State>
          <button
            type="button"
            className="exo-set-btn"
            disabled={busy !== null}
            onClick={() => arm(view.consent === 'given'
              ? {
                key: 'consent', value: 'stop', verb: 'Stop allowing', text: 'Stop allowing changes to this game?',
                also: view.restorable > 0 ? { verb: 'Stop and restore', value: 'restore', blocked: gate } : undefined,
              }
              : {
                key: 'consent', value: 'allow', verb: 'Allow, at my risk', danger: true,
                text: `This game uses ${view.antiCheatName}. Changing its files can get your account banned, and that risk is yours.`,
              })}
          >
            {view.consent === 'given' ? 'Stop allowing' : 'Allow changes'}
          </button>
        </PropRow>
      )}
      {view.restorable > 0 && (
        <PropRow label="Upscaler files" detail="Exo keeps the game's own files when it updates them." line={line('restore')}>
          <Spinner on={busy === 'restore'} />
          <button
            type="button"
            className="exo-set-btn"
            disabled={!!gate || busy !== null}
            aria-label={gate ? `Restore the originals: ${gate}` : undefined}
            onClick={() => arm({ key: 'restore', value: '', verb: 'Restore', text: 'Puts the game\'s own upscaler files back.' })}
          >
            Restore originals
          </button>
        </PropRow>
      )}
    </>
  )
}

function UpdatesSection({ props, busy, apply, line }: Rows) {
  return (
    <>
      {props.autoUpdate !== null && (
        <PropRow label="Automatic updates" line={line('autoUpdate')}>
          <Spinner on={busy === 'autoUpdate'} />
          <Choice label="Automatic updates" value={props.autoUpdate} choices={props.autoUpdateChoices} disabled={busy !== null}
            onChange={value => void apply('autoUpdate', value)} />
        </PropRow>
      )}
      {props.background !== null && (
        <PropRow label="Downloads while playing" line={line('background')}>
          <Spinner on={busy === 'background'} />
          <Choice label="Downloads while playing" value={props.background} choices={props.backgroundChoices} disabled={busy !== null}
            onChange={value => void apply('background', value)} />
        </PropRow>
      )}
    </>
  )
}

function BetasSection({ props, store, locked, busy, arm, apply, line }: Rows) {
  if (props.beta === null) return null
  return (
    <>
      <PropRow label="Beta" line={line('beta')}>
        <Spinner on={busy === 'beta'} />
        <Choice
          label="Beta"
          value={props.beta}
          choices={props.betaChoices}
          disabled={locked || busy !== null}
          onChange={value => arm({
            key: 'beta', value, verb: 'Switch',
            text: value ? `${label(props.betaChoices, value)}. ${store} downloads its build.` : `The current release. ${store} downloads it.`,
          })}
        />
      </PropRow>
      {props.betaCode && (
        <AccessCode busy={busy === 'betaCode'} disabled={locked || busy !== null} onCheck={code => apply('betaCode', '', code)} line={line('betaCode')} />
      )}
    </>
  )
}

/** Every DLC in two columns when there are many: a switch where the store has one, else where it stands. */
function ContentSection({ props, locked, busy, arm, line, pending }: Rows & { pending: Pending | null }) {
  const content = props.content ?? []
  return (
    <div className={`exo-props-content${content.length > 6 ? ' is-columns' : ''}`}>
      {content.map((item) => {
        const row = `content:${item.id}`
        // An armed switch shows where it is going until the change is confirmed or canceled.
        const armed = pending?.key === 'content' && rowOf(pending) === row
        return (
          <PropRow key={item.id} label={item.name} detail={item.toggle && item.sizeBytes ? formatSize(item.sizeBytes) : undefined} line={line(row)}>
            {item.toggle ? (
              <>
                <Spinner on={busy === row} />
                <Switch
                  checked={armed ? !item.enabled : item.enabled}
                  label={item.name}
                  disabled={locked || busy !== null}
                  onChange={(on) => {
                    arm(on === item.enabled ? null : {
                      key: 'content', value: `${item.id}:${on ? 'on' : 'off'}`, verb: on ? 'Download' : 'Remove', danger: !on,
                      text: sizeLine(on ? 'Downloads' : 'Frees', item.sizeBytes),
                    })
                  }}
                />
              </>
            ) : item.state ? <State>{item.state}</State> : null}
          </PropRow>
        )
      })}
    </div>
  )
}

/** Exo's own launch options for the game, saved on Enter or when the field loses focus. */
function LaunchOptions({ value, busy, onSave, line }: { value: string; busy: boolean; onSave: (value: string) => Promise<boolean>; line: ReactNode }) {
  const [draft, setDraft] = useState(value)
  useEffect(() => setDraft(value), [value])
  const save = () => { if (draft.trim() !== value.trim()) void onSave(draft) }
  return (
    <PropRow label="Launch options" line={line}>
      <Spinner on={busy} />
      <input
        className="exo-field exo-props-field"
        aria-label="Launch options"
        spellCheck={false}
        value={draft}
        maxLength={1024}
        onChange={event => setDraft(event.target.value)}
        onBlur={save}
        onKeyDown={(event) => { if (event.key === 'Enter') save() }}
      />
    </PropRow>
  )
}

/** A private beta's access code; it goes to the store and is never kept. */
function AccessCode({ busy, disabled, onCheck, line }: { busy: boolean; disabled: boolean; onCheck: (code: string) => Promise<boolean>; line: ReactNode }) {
  const [code, setCode] = useState('')
  const check = () => {
    const value = code.trim()
    if (value) void onCheck(value).then((ok) => { if (ok) setCode('') })
  }
  return (
    <PropRow label="Access code" line={line}>
      <input
        className="exo-field exo-props-field"
        aria-label="Access code"
        type="password"
        autoComplete="off"
        value={code}
        maxLength={256}
        disabled={disabled}
        onChange={event => setCode(event.target.value)}
        onKeyDown={(event) => { if (event.key === 'Enter') check() }}
      />
      <button type="button" className="exo-set-btn" disabled={disabled || !code.trim()} onClick={check}>
        <Spinner on={busy} />Check
      </button>
    </PropRow>
  )
}
