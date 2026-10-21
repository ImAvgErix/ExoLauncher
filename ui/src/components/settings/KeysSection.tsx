import { memo, useState } from 'react'
import { host, type LauncherSettings } from '../../lib/host'
import { HoursRows } from './HoursRows'
import { Row, Section, State } from './parts'

type KeyField =
  | 'steamWebApiKey'
  | 'itchApiKey'
  | 'steamGridDbKey'
  | 'igdbTwitchClientId'
  | 'igdbTwitchClientSecret'

/** A saved key reads as a state; the field only opens to add or replace one. */
function KeyRow({ label, detail, field, saved, getUrl, onSettings }: {
  label: string
  detail: string
  field: KeyField
  saved: boolean
  getUrl?: string
  onSettings: (next: LauncherSettings) => void
}) {
  const [draft, setDraft] = useState('')
  const [editing, setEditing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function persist(value: string) {
    setBusy(true)
    setError(null)
    try {
      const next = await host.setSettings({ [field]: value.trim() })
      onSettings(next)
      setDraft('')
      setEditing(false)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : `${label} could not be saved.`)
    } finally {
      setBusy(false)
    }
  }

  const open = editing || !saved
  return (
    <Row label={label} detail={detail} error={error}>
      {open ? (
        <form
          className="exo-set-key-form"
          onSubmit={event => { event.preventDefault(); if (draft.trim()) void persist(draft) }}
        >
          <input
            type="password"
            className="exo-field"
            value={draft}
            autoComplete="off"
            spellCheck={false}
            placeholder="Paste here"
            aria-label={label}
            disabled={busy}
            onChange={event => setDraft(event.target.value)}
          />
          <button type="submit" className="exo-set-btn" disabled={busy || !draft.trim()}>
            {busy ? 'Saving…' : 'Save'}
          </button>
          {editing ? (
            <button type="button" className="exo-set-btn" disabled={busy} onClick={() => { setEditing(false); setDraft('') }}>Cancel</button>
          ) : getUrl ? (
            <button type="button" className="exo-set-btn" onClick={() => void host.openUrl(getUrl)}>Get one</button>
          ) : null}
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

function RiotIdRow({ settings, onSettings }: {
  settings: LauncherSettings | null
  onSettings: (next: LauncherSettings) => void
}) {
  const saved = settings?.riotId?.trim() || ''
  const [draft, setDraft] = useState(saved)
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<{ text: string; error: boolean } | null>(null)
  const valid = /^[^#]+#[^#]{2,}$/.test(draft.trim())

  async function save(value: string) {
    setBusy(true)
    setNote(null)
    try {
      const next = await host.setSettings({ riotId: value })
      onSettings(next)
      if (!value) { setDraft(''); return }
      const refresh = await host.riotHoursRefresh(value)
      onSettings(await host.getSettings())
      setNote({ text: refresh.message ?? (refresh.ok ? 'Hours imported.' : 'Hours could not be imported.'), error: !refresh.ok })
    } catch (cause) {
      setNote({ text: cause instanceof Error ? cause.message : 'The Riot ID could not be saved.', error: true })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Row
      label="Riot ID"
      detail={note && !note.error ? note.text : 'VALORANT and League hours'}
      error={note?.error ? note.text : null}
    >
      <form className="exo-set-key-form" onSubmit={event => { event.preventDefault(); if (valid) void save(draft.trim()) }}>
        <input
          type="text"
          className="exo-field"
          value={draft}
          autoComplete="off"
          spellCheck={false}
          placeholder="Name#TAG"
          aria-label="Riot ID"
          disabled={busy}
          onChange={event => setDraft(event.target.value)}
        />
        <button type="submit" className="exo-set-btn" disabled={busy || !valid || draft.trim() === saved}>
          {busy ? 'Saving…' : 'Save'}
        </button>
        {saved ? (
          <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void save('')}>Remove</button>
        ) : null}
      </form>
    </Row>
  )
}

function IgdbConnect({ settings, onSettings }: {
  settings: LauncherSettings | null
  onSettings: (next: LauncherSettings) => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const ready = settings?.igdbTwitchClientIdSet === true && settings?.igdbTwitchClientSecretSet === true
  const connected = settings?.igdbTwitchConnected === true
  return (
    <Row label="IGDB game details" detail="Developer build only." error={error}>
      <State on={connected}>{connected ? 'Connected' : ready ? 'Not verified' : 'Needs both values'}</State>
      <button
        type="button"
        className="exo-set-btn"
        disabled={busy || !ready}
        onClick={() => {
          setBusy(true)
          setError(null)
          void host.twitchConnect()
            .then(async result => {
              if (!result.ok) { setError(result.message ?? 'IGDB could not verify these credentials.'); return }
              onSettings(await host.getSettings())
            })
            .catch((cause: unknown) => setError(cause instanceof Error ? cause.message : 'IGDB could not verify these credentials.'))
            .finally(() => setBusy(false))
        }}
      >
        {busy ? 'Checking…' : 'Verify'}
      </button>
    </Row>
  )
}

export const KeysSection = memo(function KeysSection({ settings, onSettings }: {
  settings: LauncherSettings | null
  onSettings: (next: LauncherSettings) => void
}) {
  return (
    <Section id="keys" title="Optional keys">
      <KeyRow
        label="Steam Web API key"
        detail="Optional. Games and achievements without Steam open"
        field="steamWebApiKey"
        saved={settings?.steamWebApiKeySet === true}
        getUrl="https://steamcommunity.com/dev/apikey"
        onSettings={onSettings}
      />
      <KeyRow
        label="itch.io API key"
        detail="Lists games you own on itch.io."
        field="itchApiKey"
        saved={settings?.itchApiKeySet === true}
        getUrl="https://itch.io/user/settings/api-keys"
        onSettings={onSettings}
      />
      <KeyRow
        label="SteamGridDB API key"
        detail="Better cover art for games outside Steam."
        field="steamGridDbKey"
        saved={settings?.steamGridDbKeySet === true}
        getUrl="https://www.steamgriddb.com/profile/preferences/api"
        onSettings={onSettings}
      />
      <RiotIdRow settings={settings} onSettings={onSettings} />
      <HoursRows settings={settings} onSettings={onSettings} />
      {settings?.developerKeysVisible === true ? (
        <>
          <KeyRow
            label="Twitch client ID"
            detail="Exo's IGDB app."
            field="igdbTwitchClientId"
            saved={settings?.igdbTwitchClientIdSet === true}
            getUrl="https://dev.twitch.tv/console/apps"
            onSettings={onSettings}
          />
          <KeyRow
            label="Twitch client secret"
            detail="Exo's IGDB app."
            field="igdbTwitchClientSecret"
            saved={settings?.igdbTwitchClientSecretSet === true}
            onSettings={onSettings}
          />
          <IgdbConnect settings={settings} onSettings={onSettings} />
        </>
      ) : null}
    </Section>
  )
})
