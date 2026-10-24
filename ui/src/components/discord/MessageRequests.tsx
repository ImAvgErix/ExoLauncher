import { useEffect, useState } from 'react'
import { host } from '../../lib/host'
import type { DiscordDm, DiscordHome, DiscordMessage } from '../../lib/discord'
import { Icons } from './icons'
import { Avatar } from './presence'

const FAILED = 'Uh oh! Something went wrong while updating this message request. Please wait a moment and try again.'

// Accept or Ignore a request; Discord's words when it doesn't take.
function useAnswer(channelId: string) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const answer = (accept: boolean) => {
    setBusy(true); setError(null)
    void host.discordChannel(channelId, accept ? 'accept' : 'ignore').then(result => {
      setBusy(false)
      if (!result.ok) setError(result.message ?? FAILED)
    }).catch(() => { setBusy(false); setError(FAILED) })
  }
  return { busy, error, answer }
}

/**
 * Discord's Message Requests: DMs from people you aren't friends with (Requests), and the ones Discord filed as Spam,
 * each with its latest message, Accept and Ignore, laid out as the Friends page. Accepted, it joins your DMs; ignored,
 * it goes away. A request opens to read, its reply waiting for Accept.
 */
export function MessageRequestsPage({ home, onOpenDm }: { home: DiscordHome | null; onOpenDm: (id: string) => void }) {
  const [tab, setTab] = useState<'requests' | 'spam'>('requests')
  const requests = (home?.dms ?? []).filter(d => d.request && !d.spam)
  const spam = (home?.dms ?? []).filter(d => d.spam)
  const shown = tab === 'requests' ? requests : spam
  return (
    <section className="exo-dc-chat exo-dc-friends" aria-label="Message requests">
      <header className="exo-dc-chat-head">
        <Icons.Requests size={22} />
        <strong>Message requests</strong>
        <span className="exo-dc-head-line" aria-hidden="true" />
        <span className="exo-dc-tabs" role="group" aria-label="Message requests">
          <button type="button" className="exo-dc-tab" aria-pressed={tab === 'requests'} onClick={() => setTab('requests')}>Requests{requests.length ? <span className="exo-dc-badge">{requests.length}</span> : null}</button>
          <button type="button" className="exo-dc-tab" aria-pressed={tab === 'spam'} onClick={() => setTab('spam')}>Spam</button>
        </span>
      </header>
      <div className="exo-dc-friends-body">
        <div className="exo-dc-friend-list">
          <p className="exo-dc-side-label">{tab === 'requests' ? 'Requests' : 'Spam'} — {shown.length}</p>
          {shown.map(dm => <RequestRow key={dm.id} dm={dm} spam={tab === 'spam'} onOpen={() => onOpenDm(dm.id)} />)}
          {shown.length === 0 ? <p className="exo-dc-note">{tab === 'requests' ? 'There are no pending message requests.' : 'Nothing in spam.'}</p> : null}
        </div>
      </div>
    </section>
  )
}

function RequestRow({ dm, spam, onOpen }: { dm: DiscordDm; spam: boolean; onOpen: () => void }) {
  const [latest, setLatest] = useState<DiscordMessage | null>(null)
  const { busy, error, answer } = useAnswer(dm.id)
  useEffect(() => {
    let live = true
    host.discordMessages(dm.id, { peek: true }).then(result => { if (live) setLatest(result.messages?.[0] ?? null) }).catch(() => { })
    return () => { live = false }
  }, [dm.id])
  const preview = latest ? latest.content || (latest.attachments.length ? 'Attachment' : 'Embed') : ''
  return (
    <div className="exo-dc-friend" data-dc-user={dm.userId ?? undefined} data-dc-name={dm.name}>
      <button type="button" className="exo-dc-avatar-btn" aria-label={`Open ${dm.name}`} onClick={onOpen}>
        <Avatar src={dm.avatar} name={dm.name} status={null} size={36} look={dm.look} />
      </button>
      <button type="button" className="exo-dc-friend-names exo-dc-request-open" onClick={onOpen}>
        <strong>{dm.name}</strong>
        <span className={error ? 'is-bad' : undefined}>{error ?? preview}</span>
      </button>
      <button type="button" className="exo-dc-round-btn is-good" aria-label={spam ? `Mark ${dm.name} as not spam` : `Accept ${dm.name}`} disabled={busy} onClick={() => answer(true)}>✓</button>
      <button type="button" className="exo-dc-round-btn is-bad" aria-label={`Ignore ${dm.name}`} disabled={busy} onClick={() => answer(false)}>×</button>
    </div>
  )
}

/** In a request's conversation, in the composer's place: Discord's line, then Ignore and Accept. */
export function RequestBanner({ channelId, spam }: { channelId: string; spam: boolean }) {
  const { busy, error, answer } = useAnswer(channelId)
  return (
    <div className="exo-dc-request-bar" role="group" aria-label="Message request">
      <span className={error ? 'is-bad' : undefined}>{error ?? 'Accept the request to send a message, view attachments, and use links.'}</span>
      <span className="exo-dc-settings-buttons">
        <button type="button" className="exo-set-btn is-compact" disabled={busy} onClick={() => answer(false)}>Ignore</button>
        <button type="button" className="exo-set-btn is-primary is-compact" disabled={busy} onClick={() => answer(true)}>{spam ? 'Mark as not spam' : 'Accept DM'}</button>
      </span>
    </div>
  )
}
