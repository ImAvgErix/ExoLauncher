import { useRef, useState } from 'react'
import { Close } from '../../brand/icons'
import { host } from '../../lib/host'
import type { DiscordChannels, DiscordEvent } from '../../lib/discord'
import { Icons } from './icons'
import { open } from './markdown'
import { useServer } from './serverPages'
import { useAccountSettings } from '../../lib/discordPrefs'
import { useLayer } from './admin'
import { SubContext, Submenu } from './UserMenu'
import { ModalBack } from './SettingsLayer'
import { useDismiss } from '../../lib/dismiss'
import { notify } from '../../lib/notify'
import { confirm } from '../ui/Dialog'
import { Skeleton } from '../ui/Skeleton'

type Item = { event: DiscordEvent; interested: boolean }

/** Discord's Events dialog: the server's upcoming events, Interested, Copy Link and the ⋯ menu; Create Event for managers. */
/** create: opened from the server menu's Create Event, straight to a new event. */
export function EventsDialog({ guildId, list, create = false, onClose }: { guildId: string; list: DiscordChannels | null; create?: boolean; onClose: () => void }) {
  const [reload, setReload] = useState(0)
  const [editing, setEditing] = useState<DiscordEvent | 'new' | null>(create ? 'new' : null)
  const { data, error } = useServer(guildId, 'events', reload)
  const events = data?.events
  const again = () => setReload(r => r + 1)
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  return (
    <ModalBack onClose={onClose}>
      <div ref={box} className="exo-dc-modal exo-dc-events" role="dialog" aria-modal="true" aria-label="Events" tabIndex={-1} onClick={event => event.stopPropagation()}>
        <div className="exo-dc-events-head">
          <Icons.Events size={20} />
          <strong>{events ? `${events.length} ${events.length === 1 ? 'Event' : 'Events'}` : 'Events'}</strong>
          {list?.canManageEvents ? <button type="button" className="exo-set-btn is-primary" onClick={() => setEditing('new')}>Create event</button> : null}
          <button type="button" className="exo-dc-icon-btn exo-dc-events-close" aria-label="Close" onClick={onClose}><Close size={16} /></button>
        </div>
        {error ? <p className="exo-dc-note" role="alert">{error}</p> : null}
        {!events && !error ? <Skeleton label="Loading events" /> : null}
        {events?.length === 0 ? <p className="exo-dc-note">There are no upcoming events.</p> : null}
        {events?.map(item => <EventCard key={item.event.id} item={item} guildId={guildId} list={list} onChanged={again} onEdit={() => setEditing(item.event)} />)}
      </div>
      {editing ? <EventEditor guildId={guildId} list={list} event={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); again() }} /> : null}
    </ModalBack>
  )
}

// "Starting in 58m", "Starting in 5h 3m", or the day and time, as Discord heads an event.
function when(event: DiscordEvent) {
  if (event.status === 2) return 'Live now'
  const start = Date.parse(event.scheduled_start_time)
  const minutes = Math.round((start - Date.now()) / 60_000)
  if (minutes <= 0) return 'Starting soon'
  if (minutes < 60) return `Starting in ${minutes}m`
  if (minutes < 24 * 60) return `Starting in ${Math.floor(minutes / 60)}h ${minutes % 60}m`
  return new Date(start).toLocaleString(undefined, { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })
}

const link = (guildId: string, event: DiscordEvent) => `https://discord.com/events/${guildId}/${event.id}`

function EventCard({ item, guildId, list, onChanged, onEdit }: { item: Item; guildId: string; list: DiscordChannels | null; onChanged: () => void; onEdit: () => void }) {
  const { event, interested } = item
  const [note, setNote] = useState<string | null>(null)
  const channel = event.channel_id ? list?.channels.find(c => c.id === event.channel_id) : undefined
  const run = (work: ReturnType<typeof host.discordServer>) =>
    void work.then(result => result.ok ? onChanged() : setNote(result.message ?? 'Discord did not take that.')).catch(() => setNote('Discord did not take that.'))
  return (
    <div className="exo-dc-event">
      <div className="exo-dc-event-top">
        <span className={event.status === 2 ? 'exo-dc-event-when is-live' : 'exo-dc-event-when'}><Icons.Events size={16} />{when(event)}</span>
        {event.user_count ? <span className="exo-dc-event-count"><Icons.Members size={14} />{event.user_count}</span> : null}
      </div>
      <strong className="exo-dc-event-name">{event.name}</strong>
      {event.description ? <p className="exo-dc-event-about">{event.description}</p> : null}
      {note ? <p className="exo-dc-note" role="alert">{note}</p> : null}
      <div className="exo-dc-event-foot">
        <span className="exo-dc-event-where">{channel ? <><Icons.Voice size={16} />{channel.name}</> : event.entity_metadata?.location}</span>
        <EventMenu event={event} guildId={guildId} canManage={list?.canManageEvents === true} run={run} onEdit={onEdit} />
        <button type="button" className="exo-dc-tab is-raised" onClick={() => void navigator.clipboard.writeText(link(guildId, event))
          .then(() => notify({ id: 'dc-copied', text: 'Event link copied', tone: 'good' })).catch(() => { })}>Copy link</button>
        <button type="button" className={`exo-dc-tab is-raised${interested ? ' is-good' : ''}`} aria-pressed={interested}
          onClick={() => run(host.discordServer('rsvp', { guildId, eventId: event.id, interested: !interested }))}>{interested ? <Icons.Check size={14} /> : null}Interested</button>
      </div>
    </div>
  )
}

// Discord's ⋯ on an event: Add to Calendar, Start or End Event, Edit Event, Cancel Event, Copy Event Link, Copy Event ID.
function EventMenu({ event, guildId, canManage, run, onEdit }: {
  event: DiscordEvent
  guildId: string
  canManage: boolean
  run: (work: ReturnType<typeof host.discordServer>) => void
  onEdit: () => void
}) {
  const [menu, setMenu] = useState(false)
  const [sub, setSub] = useState<string | null>(null)
  // Copy Event ID is Developer Mode's (Discord settings > Developer), as in Discord.
  const developer = useAccountSettings().developerMode
  const close = () => { setMenu(false); setSub(null) }
  const anchor = useRef<HTMLSpanElement>(null)
  // A click anywhere else closes it, as Discord's menus do.
  useDismiss(anchor, close, { on: menu })
  const status = (s: 2 | 3 | 4) => { close(); run(host.discordServer('eventStatus', { guildId, eventId: event.id, status: s })) }
  const cancel = async () => {
    close()
    if (await confirm({ title: 'Cancel this event?', body: `${event.name} is canceled for everyone. This cannot be undone.`, action: 'Cancel event', cancel: 'Keep event', danger: true })) status(4)
  }
  const copy = (text: string) => { void navigator.clipboard.writeText(text).catch(() => { }); close() }
  return (
    <span ref={anchor} className="exo-dc-menu-anchor">
      <button type="button" className="exo-dc-icon-btn" aria-label="More" aria-expanded={menu} onClick={() => menu ? close() : setMenu(true)}><Icons.More size={18} /></button>
      {menu ? (
        <SubContext.Provider value={{ open: sub, show: setSub }}>
        <div className="exo-dc-menu is-right" role="menu">
          <Submenu label="Add to calendar"><Calendar event={event} guildId={guildId} onDone={close} /></Submenu>
          {canManage && event.status === 1 ? <button type="button" role="menuitem" onClick={() => status(2)}>Start event</button> : null}
          {canManage && event.status === 2 ? <button type="button" role="menuitem" onClick={() => status(3)}>End event</button> : null}
          {canManage ? <button type="button" role="menuitem" onClick={() => { close(); onEdit() }}>Edit event</button> : null}
          {canManage && event.status === 1 ? (
            <button type="button" role="menuitem" className="is-danger" onClick={() => void cancel()}>Cancel event</button>
          ) : null}
          <button type="button" role="menuitem" onClick={() => copy(link(guildId, event))}>Copy event link</button>
          {developer ? <button type="button" role="menuitem" onClick={() => copy(event.id)}><Icons.CopyId size={16} />Copy event ID</button> : null}
        </div>
        </SubContext.Provider>
      ) : null}
    </span>
  )
}

// Add to Calendar: Google, an ICS file in Downloads, Yahoo or Outlook, the ones Discord offers.
function Calendar({ event, guildId, onDone }: { event: DiscordEvent; guildId: string; onDone: () => void }) {
  const start = new Date(event.scheduled_start_time)
  const end = new Date(event.scheduled_end_time ? Date.parse(event.scheduled_end_time) : start.getTime() + 3_600_000)
  const stamp = (d: Date) => d.toISOString().replace(/[-:]/g, '').replace(/\.\d{3}/, '')
  const details = `${event.description ?? ''}\n\n${link(guildId, event)}`.trim()
  const place = event.entity_metadata?.location ?? ''
  const q = (o: Record<string, string>) => new URLSearchParams(o).toString()
  const go = (url: string) => { open(url); onDone() }
  const ics = () => {
    onDone()
    const failed = (why?: string | null) => notify({ text: why ?? 'Discord did not send the ICS file.', tone: 'bad' })
    void host.discordServer('ics', { guildId, eventId: event.id }).then(result => result.ok ? notify({ text: 'Saved to Downloads', tone: 'good' }) : failed(result.message), () => failed())
  }
  return (
    <>
      <button type="button" role="menuitem" onClick={() => go(`https://calendar.google.com/calendar/render?${q({ action: 'TEMPLATE', text: event.name, dates: `${stamp(start)}/${stamp(end)}`, details, location: place })}`)}>Add to Google calendar</button>
      <button type="button" role="menuitem" onClick={ics}>Download ICS file</button>
      <button type="button" role="menuitem" onClick={() => go(`https://calendar.yahoo.com/?${q({ v: '60', title: event.name, st: stamp(start), et: stamp(end), desc: details, in_loc: place })}`)}>Add to yahoo</button>
      <button type="button" role="menuitem" onClick={() => go(`https://outlook.live.com/calendar/0/deeplink/compose?${q({ path: '/calendar/action/compose', rru: 'addevent', subject: event.name, startdt: start.toISOString(), enddt: end.toISOString(), body: details, location: place })}`)}>Add to outlook</button>
    </>
  )
}

// Discord's Create Event (and Edit Event): where (a voice channel or somewhere else), what, and when.
function EventEditor({ guildId, list, event, onClose, onSaved }: { guildId: string; list: DiscordChannels | null; event: DiscordEvent | null; onClose: () => void; onSaved: () => void }) {
  const voices = (list?.channels ?? []).filter(c => c.kind === 'voice')
  const [first] = useState(() => initial(event, voices[0]?.id))
  const [where, setWhere] = useState(first.where)
  const [location, setLocation] = useState(first.location)
  const [name, setName] = useState(first.name)
  const [description, setDescription] = useState(first.description)
  const [start, setStart] = useState(first.start)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const ready = name.trim() && start && (where !== 'elsewhere' || location.trim())
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  async function save() {
    if (!ready || busy) return
    setBusy(true)
    try {
      const result = await host.discordServer('createEvent', {
        // Editing keeps the event's end (Somewhere Else needs one); the host moves it on when the new start passes it.
        guildId, ...(event ? { eventId: event.id } : {}), ...(event?.scheduled_end_time ? { end: event.scheduled_end_time } : {}),
        name: name.trim(), description: description.trim(), start: new Date(start).toISOString(),
        ...(where === 'elsewhere' ? { location: location.trim() } : { channelId: where }),
      })
      if (result.ok) onSaved()
      else setError(result.message ?? 'Discord did not save that event.')
    } catch { setError('Discord did not save that event.') } finally { setBusy(false) }
  }
  return (
    <ModalBack top onClose={onClose}>
      <div ref={box} className="exo-dc-modal" role="dialog" aria-modal="true" aria-label={event ? 'Edit event' : 'Create event'} tabIndex={-1} onClick={e => e.stopPropagation()}>
        <strong>{event ? 'Edit event' : 'Create event'}</strong>
        <Where voices={voices} where={where} location={location} onWhere={setWhere} onLocation={setLocation} />
        <label className="exo-dc-side-label" htmlFor="exo-dc-event-name">Event topic</label>
        <input id="exo-dc-event-name" className="exo-dc-modal-input" placeholder="What's your event?" value={name} autoFocus onChange={e => setName(e.target.value)} />
        <label className="exo-dc-side-label" htmlFor="exo-dc-event-start">Start</label>
        <input id="exo-dc-event-start" className="exo-dc-modal-input" type="datetime-local" value={start} onChange={e => setStart(e.target.value)} />
        <label className="exo-dc-side-label" htmlFor="exo-dc-event-about">Description</label>
        <textarea id="exo-dc-event-about" className="exo-dc-modal-input" rows={3} placeholder="Tell people a little more about your event." value={description} onChange={e => setDescription(e.target.value)} />
        {error ? <p className="is-bad" role="alert">{error}</p> : null}
        <div className="exo-dc-modal-row">
          <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
          <button type="button" className="exo-set-btn is-primary" disabled={!ready || busy} onClick={() => void save()}>{event ? 'Save event' : 'Create event'}</button>
        </div>
      </div>
    </ModalBack>
  )
}

// The editor's first values: the event's, or a new event in the first voice channel at the next hour.
function initial(event: DiscordEvent | null, voice: string | undefined) {
  const d = event ? new Date(event.scheduled_start_time) : new Date(Date.now() + 3_600_000)
  if (!event) d.setMinutes(0, 0, 0)
  return {
    where: event ? event.channel_id ?? 'elsewhere' : voice ?? 'elsewhere',
    location: event?.entity_metadata?.location ?? '',
    name: event?.name ?? '',
    description: event?.description ?? '',
    start: toLocalInput(d),
  }
}

function Where({ voices, where, location, onWhere, onLocation }: {
  voices: DiscordChannels['channels']
  where: string
  location: string
  onWhere: (where: string) => void
  onLocation: (location: string) => void
}) {
  return (
    <>
      <p className="exo-dc-side-label">Where is your event?</p>
      {voices.map(c => (
        <button key={c.id} type="button" className="exo-dc-card" aria-pressed={where === c.id} onClick={() => onWhere(c.id)}>
          <span className="exo-dc-hash" aria-hidden="true"><Icons.Voice size={18} /></span>
          <span className="exo-dc-friend-names"><strong>{c.name}</strong></span>
        </button>
      ))}
      <button type="button" className="exo-dc-card" aria-pressed={where === 'elsewhere'} onClick={() => onWhere('elsewhere')}>
        <span className="exo-dc-friend-names"><strong>Somewhere else</strong><span>Text channel, external link or in-person location</span></span>
      </button>
      {where === 'elsewhere' ? <input className="exo-dc-modal-input" placeholder="Enter a location" aria-label="Location" value={location} onChange={e => onLocation(e.target.value)} /> : null}
    </>
  )
}

// A datetime-local input's value for this moment, in local time.
function toLocalInput(d: Date) {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}
