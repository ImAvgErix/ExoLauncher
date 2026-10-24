import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react'
import { ChevronRight } from '../../brand/icons'
import { host, onHostEvent } from '../../lib/host'
import type { DiscordChannels, DiscordHome } from '../../lib/discord'
import { Switch } from '../Switch'
import { admin, Confirm, StrangerConfirm, useLayer } from './admin'
import { DmClose, DmCopyId, DmLead, DmMuteRow, readDm, type DmInfo } from './dmMenu'
import { showFullProfile } from './profileParts'
import { Icons } from './icons'
import { useAccountSettings } from '../../lib/discordPrefs'
import { appIcon, appsPlace, runCommand } from '../../lib/discordApps'
import { useContextCommands } from './AppCommands'
import { CallPersonItems } from './voice'
import { useDismiss } from '../../lib/dismiss'

type Person = {
  ok: boolean; known: boolean; me: boolean; owner: boolean; outranks: boolean; timedOut: boolean; mfa?: boolean
  nick: string | null; name?: string | null; username?: string | null; roles: string[]
  choices: { id: string; name: string; color: string | null; assignable: boolean }[]
  can: { roles: boolean; nick: boolean; timeout: boolean; kick: boolean; ban: boolean; mute: boolean; deafen: boolean; move: boolean; transfer?: boolean }
  voice: { channelId: string; mute: boolean; deaf: boolean } | null
  moveTo: { id: string; name: string }[]
}
// call: opened on someone in a call (a voice channel's list, a call's tile), where Discord adds its call items.
type At = { userId: string; x: number; y: number; name: string; username: string | null; call?: boolean; dm?: DmInfo }
type PersonKind = 'nick' | 'kick' | 'ban' | 'transfer'
type RelationKind = 'block' | 'ignore' | 'unfriend' | 'stranger'
type Dialog = PersonKind | RelationKind | null
const RELATION_KINDS: readonly string[] = ['block', 'ignore', 'unfriend', 'stranger']
// Discord's relationship with them (0 none, 1 friends, 2 blocked, 3 their request, 4 yours), whether you ignored them, and a bot.
type Relation = { relationship: number; ignored: boolean; bot: boolean }
// The open submenu (its label), shared by the menu's rows: one open at a time, as in Discord.
type Sub = { open: string | null; show: React.Dispatch<React.SetStateAction<string | null>> }
export const SubContext = createContext<Sub>({ open: null, show: () => { } })
// Hover intent, as in Discord: while one submenu is open, passing over another submenu's row on the way into it switches
// only after a moment, and reaching the open one cancels the switch. One person menu is open at a time.
let pendingSwitch = 0

// The person menu's own rows (not a submenu's), or a submenu's.
const rowsOf = (menu: Element) => Array.from(menu.querySelectorAll<HTMLElement>(':scope > [role^="menuitem"], :scope > .exo-dc-ctx-parent > [role^="menuitem"]'))
  .filter(row => !(row as HTMLButtonElement).disabled)

// Up and Down move through the rows (Home, End to the ends), Right opens a submenu, Left goes back to its row.
function arrowKeys(event: ReactKeyboardEvent<HTMLDivElement>, sub: Sub) {
  const row = event.target as HTMLElement
  const menu = row.closest('[role="menu"]')
  if (!menu) return
  const rows = rowsOf(menu)
  const at = rows.indexOf(row)
  const to = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: rows.length - 1 }[event.key]
  if (to !== undefined) {
    event.preventDefault()
    rows[(to + rows.length) % rows.length]?.focus()
  } else if (event.key === 'ArrowRight' && row.getAttribute('aria-haspopup') === 'menu') {
    event.preventDefault()
    row.click()
  } else if (event.key === 'ArrowLeft' && menu.classList.contains('exo-dc-ctx-sub')) {
    event.preventDefault()
    event.stopPropagation()
    sub.show(null)
    menu.parentElement?.querySelector<HTMLElement>(':scope > [role="menuitem"]')?.focus()
  }
}

/** Opens Discord's person menu for someone at a point (the Members page's More button). */
export function openUserMenu(userId: string, x: number, y: number, name = '', username: string | null = null) {
  window.dispatchEvent(new CustomEvent<At>('exo-dc-user-menu', { detail: { userId, x, y, name, username } }))
}

/**
 * Discord's person menu: right-click anyone (a name or picture marked data-dc-user) anywhere in the room, or a
 * profile's More. In Discord's order: Profile, Mention, Message, Start a Call; on someone in a call, User Volume and
 * Mute; then nickname, apps, Add Friend (or Remove Friend), Ignore and Block; then in a server what the user may do
 * to them: roles and move, server mute and deafen, disconnect, timeout, kick and ban; then Copy Username and, in
 * Developer Mode, Copy User ID.
 */
export function UserMenuHost({ place, list, home }: { place: string; list: DiscordChannels | null; home: DiscordHome | null }) {
  const [at, setAt] = useState<At | null>(null)
  // The room's place: a server's id, or '@me' for Direct messages.
  const guildId = place === '@me' ? null : place
  useEffect(() => {
    const menu = (event: MouseEvent) => {
      const el = (event.target as Element | null)?.closest?.('[data-dc-user]')
      const id = el?.getAttribute('data-dc-user')
      if (!el || !id || !el.closest('.exo-dc') || window.getSelection()?.toString()) return
      event.preventDefault()
      setAt({ userId: id, x: event.clientX, y: event.clientY, name: el.getAttribute('data-dc-name') ?? '', username: el.getAttribute('data-dc-username'), call: el.hasAttribute('data-dc-call'), dm: readDm(el) })
    }
    const open = (event: Event) => setAt((event as CustomEvent<At>).detail)
    document.addEventListener('contextmenu', menu)
    window.addEventListener('exo-dc-user-menu', open)
    return () => { document.removeEventListener('contextmenu', menu); window.removeEventListener('exo-dc-user-menu', open) }
  }, [])
  const close = useCallback(() => setAt(null), [])
  if (!at) return null
  return <UserMenu key={`${at.userId}:${at.x}:${at.y}`} at={at} guildId={guildId} serverName={list?.name ?? null} meId={home?.me?.id ?? null} onClose={close} />
}

// The person as Exo knows them on this server, asked again when Discord sends their member.
function usePerson(guildId: string | null, userId: string) {
  const [person, setPerson] = useState<Person | null>(null)
  useEffect(() => {
    if (!guildId) return
    let live = true
    const load = () => void admin<Person>('person', { guildId, userId }).then(result => { if (live && result.data?.ok) setPerson(result.data) })
    load()
    const off = onHostEvent('discord.colors', event => { if (event.guildId === guildId) load() })
    return () => { live = false; off() }
  }, [guildId, userId])
  return [person, setPerson] as const
}

function UserMenu({ at, guildId, serverName, meId, onClose }: { at: At; guildId: string | null; serverName: string | null; meId: string | null; onClose: () => void }) {
  const [person, setPerson] = usePerson(guildId, at.userId)
  const [dialog, setDialog] = useState<Dialog>(null)
  const [error, setError] = useState<string | null>(null)
  const me = at.userId === meId
  const relation = useRelation(at.userId, me)
  const box = useRef<HTMLDivElement>(null)
  const [subOpen, setSubOpen] = useState<string | null>(null)
  const sub: Sub = { open: subOpen, show: setSubOpen }
  // Escape closes an open submenu first (back on its row), then the menu.
  useLayer(box, () => {
    if (!subOpen) { onClose(); return }
    setSubOpen(null)
    box.current?.querySelector<HTMLElement>('[aria-expanded="true"]')?.focus()
  }, !dialog)
  useDismiss(box, onClose, { on: !dialog })
  const name = person?.nick || person?.name || at.name || 'them'
  const username = at.username ?? person?.username ?? null
  const run: Run = (action, params, after, settled) => void admin(action, { guildId, userId: at.userId, ...params }).then(result => {
    settled?.()
    if (!result.ok) { setError(result.message ?? 'Discord did not take that.'); return }
    // The Members page shows each member's roles and nickname as they are now.
    window.dispatchEvent(new Event('exo-dc-members-changed'))
    if (after) after(); else onClose()
  })
  const done = finish(onClose, setError)
  // Add Friend, Accept Friend Request, Unblock and Unignore go at once; Remove Friend, Block and Ignore ask first.
  const relate = (action: Relate) => void relateTo(at.userId, action).then(failed => {
    if (failed === STRANGER) setDialog('stranger'); else if (failed) setError(failed); else onClose()
  })
  if (dialog) return <MenuDialog dialog={dialog} at={at} guildId={guildId} serverName={serverName} person={person} name={name} username={username} onSwitch={setDialog} onClose={onClose} />
  return (
    <SubContext.Provider value={sub}>
    <div className="exo-dc-menu exo-dc-ctx" ref={box} role="menu" aria-label={name} style={{ left: at.x, top: at.y }} onKeyDown={event => arrowKeys(event, sub)}
      onPointerOver={event => {
        // Crossing the menu's own edge or another row on the way to an open submenu closes it only after a moment.
        if (!subOpen || (event.target as Element).closest('.exo-dc-ctx-parent')) return
        window.clearTimeout(pendingSwitch)
        pendingSwitch = window.setTimeout(() => setSubOpen(null), 300)
      }}>
      {/* A DM row: Discord's DM menu opens with Mark As Read. */}
      <DmLead dm={at.dm} onDone={done} />
      <PersonItems userId={at.userId} name={name} me={me} relation={relation} guildId={guildId} onClose={onClose} />
      <DmClose dm={at.dm} onDone={done} />
      {at.call ? <CallPersonItems userId={at.userId} me={me} /> : null}
      <span className="exo-dc-menu-line" role="separator" />
      {person?.can.nick ? <button type="button" role="menuitem" onClick={() => setDialog('nick')}>Change nickname</button> : null}
      <PersonApps userId={at.userId} onClose={onClose} />
      {relation ? <RelationItems relation={relation} onRelate={relate} onAsk={setDialog} /> : null}
      {person ? <ServerItems person={person} name={name} run={run} setPerson={setPerson} onDialog={setDialog} /> : null}
      <DmMuteRow dm={at.dm} username={username} name={name} onDone={done} />
      <span className="exo-dc-menu-line" role="separator" />
      <CopyItems userId={at.userId} username={username} onClose={onClose} />
      <DmCopyId dm={at.dm} onClose={onClose} />
      {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
    </div>
    </SubContext.Provider>
  )
}

// The dialog a menu row opened: a relation's question, Transfer Ownership, or Change Nickname, Kick and Ban.
function MenuDialog({ dialog, at, guildId, serverName, person, name, username, onSwitch, onClose }: {
  dialog: NonNullable<Dialog>
  at: At
  guildId: string | null
  serverName: string | null
  person: Person | null
  name: string
  username: string | null
  onSwitch: (dialog: Dialog) => void
  onClose: () => void
}) {
  if (RELATION_KINDS.includes(dialog)) return <RelationConfirm kind={dialog as RelationKind} userId={at.userId} name={username ?? name} onSwitch={onSwitch} onClose={onClose} />
  if (dialog === 'transfer') return <TransferOwnership guildId={guildId!} userId={at.userId} name={username ?? name} mfa={person?.mfa === true} onClose={onClose} />
  return <PersonDialog kind={dialog as PersonKind} guildId={guildId!} userId={at.userId} name={name} nick={person?.nick ?? null} serverName={serverName} onClose={onClose} />
}

// A DM row's action settled: the menu closes, or says why Discord refused.
const finish = (onClose: () => void, setError: (error: string) => void) => (result: { ok: boolean; message?: string | null }) => {
  if (result.ok) onClose(); else setError(result.message ?? 'Discord did not take that.')
}

// Profile, Mention, Message and Start a Call (their DM opens and rings, as Discord's; off while blocked, none for a bot).
function PersonItems({ userId, name, me, relation, guildId, onClose }: {
  userId: string
  name: string
  me: boolean
  relation: Relation | null
  guildId: string | null
  onClose: () => void
}) {
  const inDm = (event: string) => void host.discordFriend('dm', { userId }).then(result => {
    if (result.channelId) window.dispatchEvent(new CustomEvent(event, { detail: result.channelId }))
    onClose()
  }).catch(() => onClose())
  return (
    <>
      <button type="button" role="menuitem" onClick={() => { onClose(); showFullProfile(userId, undefined, guildId) }}>Profile</button>
      <button type="button" role="menuitem" onClick={() => { window.dispatchEvent(new CustomEvent('exo-dc-mention', { detail: { id: userId, name } })); onClose() }}>Mention</button>
      {me ? null : <button type="button" role="menuitem" onClick={() => inDm('exo-dc-open-dm')}>Message</button>}
      {relation && !relation.bot ? <button type="button" role="menuitem" disabled={relation.relationship === 2} onClick={() => inDm('exo-dc-call-dm')}>Start a call</button> : null}
    </>
  )
}

// Copy Username, and Copy User ID in Developer Mode (Discord settings > Developer), as in Discord.
function CopyItems({ userId, username, onClose }: { userId: string; username: string | null; onClose: () => void }) {
  const developer = useAccountSettings().developerMode
  const copy = (text: string) => { void navigator.clipboard.writeText(text).catch(() => { }); onClose() }
  return (
    <>
      {username ? <button type="button" role="menuitem" onClick={() => copy(username)}>Copy username</button> : null}
      {developer ? <button type="button" role="menuitem" onClick={() => copy(userId)}><Icons.CopyId size={16} />Copy user ID</button> : null}
    </>
  )
}

type Relate = 'accept' | 'remove' | 'block' | 'ignore' | 'unignore'
// A relationship change sent to Discord; answers Discord's refusal, or null once it took it.
// A request from someone you share no server with answers STRANGER until it is confirmed (Discord asks first too).
const STRANGER = 'stranger'
const relateTo = (userId: string, action: Relate, confirm = false) => host.discordFriend(action, { userId, confirm })
  .then(result => result.ok ? null : result.stranger ? STRANGER : result.message ?? "Discord didn't take that.", () => "Discord didn't take that.")

// What Discord sent of your relationship with them, for the friend, Ignore and Block rows (none on yourself).
function useRelation(userId: string, me: boolean) {
  const [relation, setRelation] = useState<Relation | null>(null)
  useEffect(() => {
    if (me) return
    let live = true
    void host.discordFriend('state', { userId }).then(result => {
      if (live && result.ok) setRelation({ relationship: result.relationship ?? 0, ignored: result.ignored === true, bot: result.bot === true })
    }).catch(() => { })
    return () => { live = false }
  }, [userId, me])
  return relation
}

// Discord's rows: Remove Friend for a friend, else Add Friend (Accept Friend Request for theirs, Friend Request Sent for
// yours, off while blocked); Ignore or Unignore (not while blocked); Block or Unblock. None for a bot but Block.
function RelationItems({ relation, onRelate, onAsk }: {
  relation: Relation
  onRelate: (action: Relate) => void
  onAsk: (dialog: RelationKind) => void
}) {
  const { relationship, ignored, bot } = relation
  const blocked = relationship === 2
  return (
    <>
      {bot ? null : relationship === 1
        ? <button type="button" role="menuitem" onClick={() => onAsk('unfriend')}>Remove friend</button>
        : <button type="button" role="menuitem" disabled={blocked || relationship === 4} onClick={() => onRelate('accept')}>
          {relationship === 3 ? 'Accept friend request' : relationship === 4 ? 'Friend request sent' : 'Add friend'}
        </button>}
      {bot || blocked ? null : ignored
        ? <button type="button" role="menuitem" onClick={() => onRelate('unignore')}>Unignore</button>
        : <button type="button" role="menuitem" onClick={() => onAsk('ignore')}>Ignore</button>}
      {blocked
        ? <button type="button" role="menuitem" onClick={() => onRelate('remove')}>Unblock</button>
        : <button type="button" role="menuitem" className="is-danger" onClick={() => onAsk('block')}>Block</button>}
    </>
  )
}

// Discord's Block, Ignore and Remove Friend questions, with what each does here (Block and Ignore offer each other).
function RelationConfirm({ kind, userId, name, onSwitch, onClose }: {
  kind: RelationKind
  userId: string
  name: string
  onSwitch: (kind: RelationKind) => void
  onClose: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const confirm = () => {
    setBusy(true)
    const action = kind === 'unfriend' ? 'remove' : kind === 'stranger' ? 'accept' : kind
    void relateTo(userId, action, kind === 'stranger').then(failed => { setBusy(false); if (failed) setError(failed); else onClose() })
  }
  if (kind === 'stranger') {
    return <StrangerConfirm name={name} busy={busy} error={error} onConfirm={confirm} onClose={onClose} />
  }
  if (kind === 'unfriend') {
    return (
      <Confirm title={`Remove '${name}'`} action="Remove friend" busy={busy} error={error} onConfirm={confirm} onClose={onClose}>
        <p>Are you sure you want to remove <strong>{name}</strong> from your friends?</p>
      </Confirm>
    )
  }
  const block = kind === 'block'
  return (
    <Confirm title={block ? `Block ${name}?` : `Ignore ${name}?`} action={block ? 'Block' : 'Ignore'} danger={block} busy={busy} error={error} onConfirm={confirm} onClose={onClose}>
      <p>{block ? 'Stop direct contact and limit what they view' : 'Get space without letting them know'}</p>
      {block ? (
        <ul className="exo-dc-confirm-list">
          <li>Stop direct messages</li>
          <li>Limit access to your profile and activity<span>They won't see your mutual friends, bio, or status</span></li>
          <li>They can interact with you in GDMs and servers</li>
        </ul>
      ) : (
        <ul className="exo-dc-confirm-list">
          <li>Mute their notifications<span>Their messages won't notify you</span></li>
          <li>They can still message you and see your activity<span>They won't know you ignored them</span></li>
        </ul>
      )}
      <p>{block ? 'Too much? ' : 'Not enough? '}
        <button type="button" className="exo-dc-link" onClick={() => { setError(null); onSwitch(block ? 'ignore' : 'block') }}>{block ? 'Ignore instead' : 'Block instead'}</button>
      </p>
    </Confirm>
  )
}

// Apps: the user commands where you are, each run on this person, as Discord's person menu offers them.
function PersonApps({ userId, onClose }: { userId: string; onClose: () => void }) {
  const place = appsPlace()
  const list = useContextCommands(place?.channelId ?? null, 2)
  if (!place || !list.length) return null
  return (
    <Submenu label="Apps">
      {list.map(({ command, app }) => {
        const icon = appIcon(app, 16)
        return (
          <button key={command.id} type="button" role="menuitem" onClick={() => { onClose(); void runCommand(place.channelId, { commandId: command.id, targetId: userId }) }}>
            {icon ? <img className="exo-dc-menu-icon" src={icon} alt="" draggable={false} /> : null}<span className="exo-dc-ctx-name">{command.name}</span>
          </button>
        )
      })}
    </Submenu>
  )
}

// after runs once Discord took it (else the menu closes); settled either way.
type Run = (action: string, params: Record<string, unknown>, after?: () => void, settled?: () => void) => void

const TIMEOUTS = [{ minutes: 1, label: '60 seconds' }, { minutes: 5, label: '5 minutes' }, { minutes: 10, label: '10 minutes' },
  { minutes: 60, label: '1 hour' }, { minutes: 1440, label: '1 day' }, { minutes: 10080, label: '1 week' }]

// What the user may do to this person on the server, each only with Discord's permission and above them in the role order:
// Roles and Move to, then Server Mute, Server Deafen, Disconnect, Timeout, Kick and Ban, as Discord groups them.
function ServerItems({ person, name, run, setPerson, onDialog }: {
  person: Person
  name: string
  run: Run
  setPerson: (person: Person) => void
  onDialog: (dialog: Dialog) => void
}) {
  const { can, voice } = person
  const roles = person.choices.filter(r => r.assignable || person.roles.includes(r.id))
  // One role given or taken at a time, as Discord does, so a quick second tick never races the first.
  const [saving, setSaving] = useState(false)
  const toggleRole = (id: string, on: boolean) => {
    if (saving) return
    const next = on ? [...person.roles, id] : person.roles.filter(r => r !== id)
    setSaving(true)
    run(on ? 'addRole' : 'removeRole', { roleId: id }, () => setPerson({ ...person, roles: next }), () => setSaving(false))
  }
  const moveTo = voice && can.move ? person.moveTo : []
  return (
    <>
      <span className="exo-dc-menu-line" role="separator" />
      {can.roles && roles.length ? (
        <Submenu label="Roles">
          {person.known ? roles.map(role => (
            <button key={role.id} type="button" role="menuitemcheckbox" aria-checked={person.roles.includes(role.id)} disabled={!role.assignable || saving}
              onClick={() => toggleRole(role.id, !person.roles.includes(role.id))}>
              <span className="exo-dc-role-dot" style={{ background: role.color ?? 'var(--dc-muted)' }} aria-hidden="true" /><span className="exo-dc-ctx-name">{role.name}</span>
              {person.roles.includes(role.id) ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
            </button>
          )) : <p className="exo-dc-note">Loading</p>}
        </Submenu>
      ) : null}
      {moveTo.length ? (
        <Submenu label="Move to">
          {moveTo.map(c => <button key={c.id} type="button" role="menuitem" onClick={() => run('voice', { channelId: c.id })}><Icons.Voice size={16} /><span className="exo-dc-ctx-name">{c.name}</span></button>)}
        </Submenu>
      ) : null}
      <span className="exo-dc-menu-line" role="separator" />
      {voice && can.mute ? <button type="button" role="menuitemcheckbox" aria-checked={voice.mute} className="is-danger" onClick={() => run('voice', { mute: !voice.mute })}>Server mute{voice.mute ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}</button> : null}
      {voice && can.deafen ? <button type="button" role="menuitemcheckbox" aria-checked={voice.deaf} className="is-danger" onClick={() => run('voice', { deaf: !voice.deaf })}>Server deafen{voice.deaf ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}</button> : null}
      {voice && can.move ? <button type="button" role="menuitem" className="is-danger" onClick={() => run('voice', { disconnect: true })}>Disconnect</button> : null}
      {can.timeout ? (
        <Submenu label={`Timeout ${name}`} danger>
          {TIMEOUTS.map(t => <button key={t.minutes} type="button" role="menuitem" onClick={() => run('timeout', { minutes: t.minutes })}>{t.label}</button>)}
          {person.timedOut ? <button type="button" role="menuitem" onClick={() => run('timeout', { minutes: 0 })}>Remove timeout</button> : null}
        </Submenu>
      ) : null}
      {can.kick ? <button type="button" role="menuitem" className="is-danger" onClick={() => onDialog('kick')}>Kick {name}</button> : null}
      {can.ban ? <button type="button" role="menuitem" className="is-danger" onClick={() => onDialog('ban')}>Ban {name}</button> : null}
      {can.transfer ? <button type="button" role="menuitem" className="is-danger" onClick={() => onDialog('transfer')}>Transfer ownership</button> : null}
    </>
  )
}

// A row that opens its choices beside the menu, as Discord's submenus do: on hover or a click (a click never shuts it),
// staying open for a moment after the pointer leaves so it can cross the gap to the choices. The choices are drawn in
// the top layer: glass there sees the page behind it, where glass inside the menu's glass would see nothing and draw
// near black. They open right of the row, or left of it when the window has no room, and stay inside the window.
export function Submenu({ label, danger, children }: { label: string; danger?: boolean; children: React.ReactNode }) {
  const { open: current, show } = useContext(SubContext)
  const open = current === label
  const row = useRef<HTMLButtonElement>(null)
  const sub = useRef<HTMLDivElement>(null)
  const leave = useRef(0)
  const keyboard = useRef(false)
  useEffect(() => () => window.clearTimeout(leave.current), [])
  useLayoutEffect(() => {
    const el = sub.current, from = row.current?.getBoundingClientRect()
    if (!open || !el || !from) return
    if (!el.matches(':popover-open')) el.showPopover()
    const { width, height } = el.getBoundingClientRect()
    el.style.left = `${from.right + 4 + width <= window.innerWidth - 8 ? from.right + 4 : Math.max(8, from.left - 4 - width)}px`
    el.style.top = `${Math.max(8, Math.min(from.top - 6, window.innerHeight - 8 - height))}px`
    // Opened from the keyboard: focus moves to its first choice.
    if (keyboard.current) { keyboard.current = false; rowsOf(el)[0]?.focus() }
  }, [open])
  const stay = () => { window.clearTimeout(leave.current); window.clearTimeout(pendingSwitch); show(label) }
  const enter = () => {
    if (!current || current === label) { stay(); return }
    window.clearTimeout(pendingSwitch)
    pendingSwitch = window.setTimeout(() => show(label), 300)
  }
  return (
    <span className="exo-dc-ctx-parent" onPointerEnter={enter}
      onPointerLeave={() => { window.clearTimeout(pendingSwitch); if (open) { window.clearTimeout(leave.current); leave.current = window.setTimeout(() => show(now => now === label ? null : now), 300) } }}>
      <button ref={row} type="button" role="menuitem" aria-haspopup="menu" aria-expanded={open} className={danger ? 'is-danger' : undefined}
        onClick={event => { keyboard.current = event.detail === 0; stay() }}>
        {label}<ChevronRight size={14} className="exo-dc-ctx-arrow" />
      </button>
      {open ? (
        // Escape closes the choices alone, back on their row (a menu's own Escape skips the top layer).
        <div ref={sub} popover="manual" className="exo-dc-menu exo-dc-ctx-sub" role="menu" aria-label={label}
          onKeyDown={event => { if (event.key !== 'Escape') return; event.preventDefault(); event.stopPropagation(); show(null); row.current?.focus() }}>
          {children}
        </div>
      ) : null}
    </span>
  )
}

// Discord's Ban: Delete Message History choices.
const DELETE_HISTORY = [{ seconds: 0, label: "Don't delete any" }, { seconds: 3600, label: 'Previous hour' }, { seconds: 21600, label: 'Previous 6 hours' },
  { seconds: 43200, label: 'Previous 12 hours' }, { seconds: 86400, label: 'Previous 24 hours' }, { seconds: 259200, label: 'Previous 3 days' },
  { seconds: 604800, label: 'Previous 7 days' }]
const BAN_REASONS = ['Suspicious or spam account', 'Compromised or hacked account', 'Breaking server rules']

// Change Nickname, Kick and Ban, worded as Discord words them.
function PersonDialog({ kind, guildId, userId, name, nick, serverName, onClose }: {
  kind: PersonKind
  guildId: string
  userId: string
  name: string
  nick: string | null
  serverName: string | null
  onClose: () => void
}) {
  const [text, setText] = useState(kind === 'nick' ? nick ?? '' : '')
  const [reason, setReason] = useState(BAN_REASONS[0])
  const [seconds, setSeconds] = useState(3600)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  function submit() {
    setBusy(true)
    const params = kind === 'nick' ? { nick: text.trim() }
      : kind === 'kick' ? { reason: text.trim() }
        : { deleteSeconds: seconds, reason: reason === 'other' ? text.trim() : reason }
    void admin(kind, { guildId, userId, ...params }).then(result => {
      setBusy(false)
      if (!result.ok) { setError(result.message ?? 'Discord did not take that.'); return }
      // The Members page shows who is still here, and their nickname as it is now.
      window.dispatchEvent(new Event('exo-dc-members-changed'))
      onClose()
    })
  }
  if (kind === 'nick') {
    return (
      <Confirm title="Change nickname" action="Save" danger={false} busy={busy} error={error} onConfirm={submit} onClose={onClose}>
        <p>Nicknames are visible to everyone on this server. Do not change them unless you are enforcing a naming system or clearing a bad nickname.</p>
        <label className="exo-dc-side-label" htmlFor="exo-dc-nick">Nickname</label>
        <input id="exo-dc-nick" className="exo-dc-modal-input" value={text} maxLength={32} placeholder={name} autoFocus onChange={event => setText(event.target.value)}
          onKeyDown={event => { if (event.key === 'Enter') submit() }} />
        {nick ? <button type="button" className="exo-dc-link exo-dc-reset" onClick={() => setText('')}>Reset nickname</button> : null}
      </Confirm>
    )
  }
  if (kind === 'kick') {
    return (
      <Confirm title={`Kick ${name} from server`} action="Kick" busy={busy} error={error} onConfirm={submit} onClose={onClose}>
        <p>Are you sure you want to kick {name} from {serverName ?? 'the server'}? They will be able to re-join again with a new invite.</p>
        <label className="exo-dc-side-label" htmlFor="exo-dc-kick-reason">Reason for kick</label>
        <textarea id="exo-dc-kick-reason" className="exo-dc-modal-input" rows={3} maxLength={512} value={text} onChange={event => setText(event.target.value)} />
      </Confirm>
    )
  }
  return (
    <Confirm title={`Ban ${name}?`} action="Ban" busy={busy || (reason === 'other' && !text.trim())} error={error} onConfirm={submit} onClose={onClose}>
      <p className="exo-dc-side-label">Reason for ban</p>
      <div className="exo-dc-options" role="radiogroup" aria-label="Reason for ban">
        {[...BAN_REASONS, 'other'].map(r => (
          <button key={r} type="button" role="radio" aria-checked={reason === r} className={`exo-dc-option${reason === r ? ' is-current' : ''}`} onClick={() => setReason(r)}>
            <span className="exo-dc-option-dot" aria-hidden="true" /><span>{r === 'other' ? 'Other' : r}</span>
          </button>
        ))}
      </div>
      {reason === 'other' ? <textarea className="exo-dc-modal-input" rows={3} maxLength={512} value={text} aria-label="Reason" autoFocus onChange={event => setText(event.target.value)} /> : null}
      <label className="exo-dc-side-label" htmlFor="exo-dc-ban-history">Delete message history</label>
      <select id="exo-dc-ban-history" className="exo-dc-select" value={seconds} onChange={event => setSeconds(Number(event.target.value))}>
        {DELETE_HISTORY.map(d => <option key={d.seconds} value={d.seconds}>{d.label}</option>)}
      </select>
    </Confirm>
  )
}

/**
 * Discord's Transfer Ownership: you acknowledge the server becomes theirs, then verify, with your 2FA (Discord's
 * Multi-Factor Authentication dialog) or, without 2FA, the code Discord emails you.
 */
function TransferOwnership({ guildId, userId, name, mfa, onClose }: { guildId: string; userId: string; name: string; mfa: boolean; onClose: () => void }) {
  const [agreed, setAgreed] = useState(false)
  const [sent, setSent] = useState(false)
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function submit() {
    setBusy(true); setError(null)
    if (!mfa && !sent) {
      const asked = await admin('transferCode', { guildId })
      setBusy(false)
      if (asked.ok) setSent(true); else setError(asked.message ?? 'Discord did not send the code.')
      return
    }
    const result = await admin('transfer', { guildId, userId, ...(mfa ? {} : { code: code.trim() }) })
    setBusy(false)
    if (result.ok) { window.dispatchEvent(new Event('exo-dc-members-changed')); onClose() }
    else if (result.message !== 'Multi-factor authentication cancelled') setError(result.message ?? 'Server ownership transfer failed')
  }
  return (
    <Confirm title="Transfer ownership" action={!mfa && !sent ? 'Send code' : 'Transfer ownership'} busy={busy || !agreed || (sent && !code.trim())} error={error}
      onConfirm={() => void submit()} onClose={onClose}>
      <span className="exo-dc-form-switch">
        <span>I acknowledge that by transferring ownership of this server to <strong>{name}</strong>, it officially belongs to them.</span>
        <Switch checked={agreed} label="I acknowledge the transfer" onChange={setAgreed} />
      </span>
      {sent ? (
        <>
          <p>Discord emailed you a code to confirm this.</p>
          <input className="exo-dc-modal-input" value={code} maxLength={16} placeholder="Confirmation code" aria-label="Confirmation code" autoFocus onChange={event => setCode(event.target.value)} />
        </>
      ) : null}
    </Confirm>
  )
}
