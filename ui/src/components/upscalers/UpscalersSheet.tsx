import { useEffect, useId, useMemo, useRef, useState, type CSSProperties } from 'react'
import { host, type DlssStatus, type Game } from '../../lib/host'
import { thumbUrl, useCoverThumbWidth } from '../../lib/coverThumb'
import { invalidateUpscalerStatus, loadUpscalerStatus } from '../../lib/upscalerCache'
import { compareVersions, upscalerView } from '../../lib/upscalers'
import { report } from '../../lib/report'
import { CHECK_PATH } from '../../brand/icons'

/** One installed copy of a game: a card, or one installed store variant of it. */
type Copy = { id: string; title: string; cover: string | null; running: boolean }

type Run =
  | { kind: 'idle' }
  | { kind: 'working' }
  | { kind: 'done'; ok: boolean; text: string }

/** One upscaler family in a game (DLSS, FSR, XeSS) and the jump it needs. */
type Family = { name: string; files: number; from: string | null; to: string | null }

/** What one game needs, from its upscaler status. */
type Need = {
  copy: Copy
  status: DlssStatus
  families: Family[]
  /** Anti-cheat without the user's OK: listed, never written until it is given (here or in the game's Properties). */
  locked: boolean
  /** The anti-cheat Exo found in the game, named when the user is asked. */
  antiCheat: string | null
}


function copiesOf(games: readonly Game[]): Copy[] {
  const seen = new Set<string>()
  const out: Copy[] = []
  for (const game of games) {
    const sources = [
      ...(game.installed ? [{ id: game.id, running: !!game.canStop }] : []),
      ...(game.variants ?? []).filter(variant => variant.installed).map(variant => ({ id: variant.id, running: !!variant.canStop })),
    ]
    for (const source of sources) {
      const key = source.id.toLowerCase()
      if (seen.has(key)) continue
      seen.add(key)
      out.push({ id: source.id, title: game.title, cover: game.coverUrl ?? null, running: source.running || !!game.canStop })
    }
  }
  return out
}

const FAMILIES = ['DLSS', 'FSR', 'XeSS'] as const

/** A scan older than this reads again when the panel shows. */
const STALE_MS = 5 * 60_000

/** "DLSS Ray Reconstruction" → DLSS; files of one family fold into one line. */
function familyOf(label: string): string {
  return FAMILIES.find(name => label.startsWith(name)) ?? label.split(' ')[0]
}

function needOf(copy: Copy, status: DlssStatus): Need | null {
  const view = upscalerView(status)
  const outdated = view.rows.filter(row => row.state === 'outdated')
  if (outdated.length === 0) return null
  if (view.consent === 'never') return null
  const latest = (label: string) => (status.items ?? []).find(item => (item.displayName || item.fileName) === label)?.latestVersion ?? null
  const families = new Map<string, Family>()
  for (const row of outdated) {
    const name = familyOf(row.label)
    const to = latest(row.label)
    const family = families.get(name) ?? { name, files: 0, from: null, to: null }
    family.files++
    // The oldest file it has and the newest it gets.
    if (row.version && (!family.from || compareVersions(row.version, family.from) < 0)) family.from = row.version
    if (to && (!family.to || compareVersions(to, family.to) > 0)) family.to = to
    families.set(name, family)
  }
  return { copy, status, families: [...families.values()], locked: view.locked, antiCheat: view.antiCheatName }
}

/**
 * System > Graphics > Upscalers: a cover card for every installed game with older DLSS, FSR or XeSS
 *   than Exo has, each showing the jump per family, and Update all bringing
 *   them up a game at a time with the card showing the work. Anti-cheat games
 *   are listed only where the user may allow changes, and are updated only
 *   once they did: in the game's Properties, or here, where Update all first
 *   names every anti-cheat and its ban risk and offers to update them too, at
 *   the user's risk, or only the others. Riot games never appear. DLSS presets
 * are System > Graphics > Driver and profile's.
 */
export function UpscalersTab({ active, games }: { active: boolean; games: readonly Game[] }) {
  const copies = useMemo(() => copiesOf(games), [games])
  const key = copies.map(copy => copy.id).join('\n')
  // Whether a game runs is read from the library now, not from when it was scanned.
  const running = useMemo(() => new Set(copies.filter(copy => copy.running).map(copy => copy.id)), [copies])
  const [needs, setNeeds] = useState<Need[] | null>(null)
  const [checked, setChecked] = useState(0)
  const [runs, setRuns] = useState<Record<string, Run>>({})
  const [updating, setUpdating] = useState(false)
  const [pass, setPass] = useState(0)
  const [asking, setAsking] = useState(false)
  const scanned = useRef<{ key: string; at: number } | null>(null)
  const live = useRef(true)
  useEffect(() => {
    live.current = true
    return () => { live.current = false }
  }, [])

  // Scans the first time the panel shows, and again when it shows after a while or the installed games changed.
  useEffect(() => {
    if (!active || updating) return
    const last = scanned.current
    if (last && last.key === key && Date.now() - last.at < STALE_MS) return
    setRuns({})
    setPass(value => value + 1)
  }, [active, key, updating])

  // Read every installed copy's status (cached for 30 s, so reopening is instant), four at a time.
  useEffect(() => {
    if (pass === 0) return
    scanned.current = { key, at: Date.now() }
    let current = true
    setChecked(0)
    const found: Need[] = []
    let cursor = 0
    const worker = async () => {
      while (current && cursor < copies.length) {
        const copy = copies[cursor++]
        try {
          const need = needOf(copy, await loadUpscalerStatus(copy.id))
          if (need) found.push(need)
        } catch (error) { report(error, 'UpscalersSheet.status', { level: 'warn' }) }
        if (current) setChecked(value => value + 1)
      }
    }
    void Promise.all(Array.from({ length: Math.min(4, Math.max(1, copies.length)) }, worker)).then(() => {
      if (!current) return
      const order = new Map(copies.map((copy, index) => [copy.id, index]))
      setNeeds(found.sort((a, b) => (order.get(a.copy.id) ?? 0) - (order.get(b.copy.id) ?? 0)))
      // A read after Update all: a game still listed that failed keeps why; one that updated starts fresh.
      setRuns(value => Object.fromEntries(Object.entries(value).filter(([, run]) => run.kind === 'done' && !run.ok)))
    })
    return () => { current = false }
    // Only a new pass reads again (above, and Check again); a running game changing does not restart the scan.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pass])

  // Not one already updating from its own card: Update all would write that game's folder twice at once.
  const idle = (need: Need) => !running.has(need.copy.id) && runs[need.copy.id]?.kind !== 'done' && runs[need.copy.id]?.kind !== 'working'
  const ready = (needs ?? []).filter(need => !need.locked && idle(need))
  const risky = (needs ?? []).filter(need => need.locked && need.antiCheat && idle(need))

  async function updateOne(need: Need): Promise<boolean> {
    setRuns(value => ({ ...value, [need.copy.id]: { kind: 'working' } }))
    let run: Run
    try {
      const result = await host.dlssApply(need.copy.id)
      const ok = result.ok && (result.failed ?? 0) === 0
      run = { kind: 'done', ok, text: ok ? 'Updated' : result.message || 'Could not update' }
    } catch (error) {
      report(error, 'UpscalersSheet.update')
      run = { kind: 'done', ok: false, text: 'Could not update' }
    }
    invalidateUpscalerStatus(need.copy.id)
    if (live.current) setRuns(value => ({ ...value, [need.copy.id]: run }))
    return run.kind === 'done' && run.ok
  }

  /**
   * Allows one anti-cheat game the way its Properties do (stored per game, tied to
   * that anti-cheat, logged with each write), then updates it.
   */
  async function allowAndUpdate(need: Need): Promise<boolean> {
    try {
      const answer = await host.dlssConsent(need.copy.id, true, need.antiCheat ?? undefined)
      if (!answer.ok) {
        if (live.current) setRuns(value => ({ ...value, [need.copy.id]: { kind: 'done', ok: false, text: answer.message || 'Not allowed' } }))
        return false
      }
    } catch (error) {
      report(error, 'UpscalersSheet.consent')
      if (live.current) setRuns(value => ({ ...value, [need.copy.id]: { kind: 'done', ok: false, text: 'Not allowed' } }))
      return false
    }
    return updateOne(need)
  }

  async function updateAll(withRisky = false) {
    if (updating) return
    setAsking(false)
    setUpdating(true)
    // One game at a time: each writes inside its own folder, and the first
    // download of a release is shared by every game after it.
    for (const need of ready) {
      if (!live.current) return
      await updateOne(need)
    }
    if (withRisky) {
      for (const need of risky) {
        if (!live.current) return
        await allowAndUpdate(need)
      }
    }
    if (!live.current) return
    setUpdating(false)
    // Read every game again: the cards stay until it lands, then whatever is still behind, else Everything is on the newest release.
    setPass(value => value + 1)
  }

  const total = copies.length
  const listed = needs ?? []
  const left = listed.filter(need => !(runs[need.copy.id]?.kind === 'done' && (runs[need.copy.id] as { ok: boolean }).ok)).length
  const done = listed.every(need => runs[need.copy.id]?.kind === 'done' || (need.locked && !need.antiCheat) || running.has(need.copy.id))

  return (
    <div className="exo-upscalers exo-gpu-pane">
      <div className="exo-gpu-bar">
        <span className="exo-gpu-bar-note">{needs !== null && left > 0 ? `${left} ${left === 1 ? 'game' : 'games'} to update` : null}</span>
        {needs !== null && (
          <UpdateButton done={done} updating={updating} asking={asking} count={ready.length + risky.length} all={listed.length}
            onCheck={() => { setRuns({}); setPass(value => value + 1) }}
            onUpdate={() => (risky.length ? setAsking(true) : void updateAll())} />
        )}
      </div>
      {asking ? (
        <RiskConfirm games={risky} others={ready.length} onCancel={() => setAsking(false)} onAll={() => void updateAll(true)} onOthers={() => void updateAll(false)} />
      ) : null}
      <div className="exo-upscalers-body">
        {(
          <>
            {needs === null ? (
              <div className="exo-upscalers-progress" role="progressbar" aria-valuemin={0} aria-valuemax={total} aria-valuenow={checked}>
                <span style={{ '--fill': total ? checked / total : 0 } as CSSProperties} />
              </div>
            ) : listed.length === 0 ? (
              <AllCurrent />
            ) : (
              <ul className="exo-upscalers-cards">
                {listed.map((need, index) => (
                  <GameCard
                    key={need.copy.id}
                    need={need}
                    index={index}
                    run={runs[need.copy.id] ?? { kind: 'idle' }}
                    running={running.has(need.copy.id)}
                    busy={updating}
                    onUpdate={() => void updateOne(need)}
                  />
                ))}
              </ul>
            )}
          </>
        )}
      </div>
    </div>
  )
}

/** The tab's one action: Update all (or how many), Updating…, or Check again once nothing is left. */
function UpdateButton({ done, updating, asking, count, all, onCheck, onUpdate }: {
  done: boolean
  updating: boolean
  asking: boolean
  count: number
  all: number
  onCheck: () => void
  onUpdate: () => void
}) {
  if (done && !updating) return <button type="button" className="exo-game-tool exo-upscaler-btn" onClick={onCheck}>Check again</button>
  return (
    <button type="button" className="exo-game-tool exo-upscaler-btn is-primary" disabled={updating || asking || count === 0} onClick={onUpdate}>
      {updating ? 'Updating…' : count === all ? 'Update all' : `Update ${count}`}
    </button>
  )
}

/**
 * Update all with anti-cheat games among them: each game and its anti-cheat by
 * name, and the risk said plainly, before anything is written. Updating them
 * too allows each one as its game page would; the others can go alone.
 */
function RiskConfirm({ games, others, onCancel, onAll, onOthers }: {
  games: Need[]
  others: number
  onCancel: () => void
  onAll: () => void
  onOthers: () => void
}) {
  const headingId = useId()
  const cancel = useRef<HTMLButtonElement>(null)
  useEffect(() => { cancel.current?.focus() }, [])
  const named = games.map(need => `${need.copy.title} (${need.antiCheat})`).join(', ')
  return (
    <div className="exo-upscaler-consent exo-upscalers-risk" role="group" aria-labelledby={headingId}>
      <p id={headingId} className="exo-upscaler-consent-title">
        {games.length === 1 ? 'One game uses anti-cheat' : `${games.length} games use anti-cheat`}: {named}. Changing their files can get your account banned, and that risk is yours.
      </p>
      <span className="exo-upscaler-consent-actions">
        <button ref={cancel} type="button" className="exo-game-tool exo-upscaler-btn" onClick={onCancel}>Cancel</button>
        {others > 0 && <button type="button" className="exo-game-tool exo-upscaler-btn" onClick={onOthers}>Only the others</button>}
        <button type="button" className="exo-game-tool exo-upscaler-btn is-armed" onClick={onAll}>Update all, at my risk</button>
      </span>
    </div>
  )
}


/** Nothing to do: a check that draws itself in. */
function AllCurrent() {
  return (
    <div className="exo-upscalers-clear">
      <svg className="exo-upscalers-clear-mark" viewBox="0 0 24 24" aria-hidden="true">
        <circle cx="12" cy="12" r="9" pathLength="1" />
        <path d="M9 12l2 2l4 -4" pathLength="1" />
      </svg>
      <p className="exo-upscalers-clear-title">Everything is on the newest release</p>
    </div>
  )
}

/**
 * One game: its cover, and one line per upscaler family with the jump it
 * gets. Work and Done (DESIGN.md › Motion) play on the cover while it
 * updates and when it lands; the new versions stay green.
 */
function GameCard({ need, index, run, running, busy, onUpdate }: { need: Need; index: number; run: Run; running: boolean; busy: boolean; onUpdate: () => void }) {
  const width = useCoverThumbWidth()
  const ok = run.kind === 'done' && run.ok
  const failed = run.kind === 'done' && !run.ok
  const note = need.locked ? 'Allow it in the game’s Properties'
    : running ? 'Close the game first'
      : failed ? run.text : null
  const state = run.kind === 'working' ? 'is-working' : ok ? 'is-done' : failed ? 'is-failed' : need.locked || running ? 'is-blocked' : ''
  return (
    <li className={`exo-upscalers-card exo-motion-arrive ${state}`} style={{ '--i': index } as CSSProperties}>
      <div className="exo-upscalers-card-cover">
        {need.copy.cover ? <img src={thumbUrl(need.copy.cover, width)} alt="" loading="lazy" decoding="async" /> : null}
        <CoverMotion working={run.kind === 'working'} done={ok} />
        {run.kind === 'idle' && !note && (
          <button type="button" className="exo-upscalers-card-update" disabled={busy} onClick={onUpdate} aria-label={`Update ${need.copy.title}`}>Update</button>
        )}
      </div>
      <div className="exo-upscalers-card-main">
        <span className="exo-upscalers-card-title">{need.copy.title}</span>
        <ul className="exo-upscalers-jumps">
          {need.families.map(family => (
            <li key={family.name} className="exo-upscalers-jump">
              <span className={`exo-upscalers-family is-${family.name.toLowerCase()}`}>{family.name}</span>
              <span className="exo-upscalers-versions">
                {!ok && <span className="exo-upscalers-from">{family.from ?? '?'}</span>}
                {!ok && <span className="exo-upscalers-arrow" aria-hidden="true">→</span>}
                <span className="exo-upscalers-to">{family.to ?? '?'}</span>
              </span>
            </li>
          ))}
        </ul>
        {note && <p className={`exo-upscalers-card-note${failed ? ' is-bad' : ''}`} aria-live="polite">{note}</p>}
      </div>
    </li>
  )
}

/** Work while the game updates, Done once it lands (DESIGN.md › Motion). */
function CoverMotion({ working, done }: { working: boolean; done: boolean }) {
  if (!working && !done) return null
  return (
    <span className={`exo-upscalers-card-motion ${done ? 'exo-motion-done' : 'exo-motion-work'}`} aria-hidden="true">
      {done && <svg className="exo-motion-check" viewBox="0 0 24 24"><path d={CHECK_PATH} pathLength="1" /></svg>}
    </span>
  )
}
