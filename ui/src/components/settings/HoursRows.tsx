import { useState } from 'react'
import { host, type LauncherSettings } from '../../lib/host'
import { Spinner } from '../ui/Spinner'
import { Row, State } from './parts'

type Note = { text: string; error: boolean } | null

/** Saves, then reads that source's hours at once so the row can say what came back. */
async function saveAndRead(
  patch: Parameters<typeof host.setSettings>[0],
  source: string,
  read: boolean,
  onSettings: (next: LauncherSettings) => void,
): Promise<Note> {
  onSettings(await host.setSettings(patch))
  if (!read) return null
  const result = await host.playtimeRefresh(source)
  return { text: result.message ?? (result.ok ? 'Hours imported.' : 'Hours could not be read.'), error: !result.ok }
}

/** Xbox time played through the player's own OpenXBL key; the key never comes back. */
function XboxRow({ saved, onSettings }: { saved: boolean; onSettings: (next: LauncherSettings) => void }) {
  const [draft, setDraft] = useState('')
  const [editing, setEditing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<Note>(null)

  async function persist(value: string) {
    setBusy(true)
    setNote(null)
    try {
      setNote(await saveAndRead({ openXblKey: value }, 'xbox', value.length > 0, onSettings))
      setDraft('')
      setEditing(false)
    } catch (cause) {
      setNote({ text: cause instanceof Error ? cause.message : 'The key could not be saved.', error: true })
    } finally {
      setBusy(false)
    }
  }

  const open = editing || !saved
  return (
    <Row
      label="Xbox time played"
      detail={note && !note.error ? note.text : undefined}
      error={note?.error ? note.text : null}
    >
      {open ? (
        <form className="exo-set-key-form" onSubmit={event => { event.preventDefault(); if (draft.trim()) void persist(draft.trim()) }}>
          <input
            type="password"
            className="exo-field"
            value={draft}
            autoComplete="off"
            spellCheck={false}
            placeholder="OpenXBL key"
            aria-label="OpenXBL key"
            disabled={busy}
            onChange={event => setDraft(event.target.value)}
          />
          <button type="submit" className="exo-set-btn" disabled={busy || !draft.trim()}>
            {busy ? <Spinner label="Saving" /> : null}Save
          </button>
          {editing ? (
            <button type="button" className="exo-set-btn" disabled={busy} onClick={() => { setEditing(false); setDraft('') }}>Cancel</button>
          ) : (
            <button type="button" className="exo-set-btn" onClick={() => void host.openUrl('https://xbl.io')}>Get one</button>
          )}
        </form>
      ) : (
        <>
          <State on>Saved</State>
          <button type="button" className="exo-set-btn" disabled={busy} onClick={() => setEditing(true)}>Replace</button>
          <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void persist('')}>Remove</button>
        </>
      )}
    </Row>
  )
}

/** Hours from services other than the stores: Xbox. */
export function HoursRows({ settings, onSettings }: {
  settings: LauncherSettings | null
  onSettings: (next: LauncherSettings) => void
}) {
  return (
    <>
      <XboxRow saved={settings?.openXblKeySet === true} onSettings={onSettings} />
    </>
  )
}
