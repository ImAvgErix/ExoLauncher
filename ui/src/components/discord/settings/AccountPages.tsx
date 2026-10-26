import { useEffect, useRef, useState } from 'react'
import { host } from '../../../lib/host'
import type { DiscordHome } from '../../../lib/discord'
import { changeAccountSettings, useAccountSettings, useStreamerHides, type CustomStatus, type DiscordAccount, type DiscordSession } from '../../../lib/discordPrefs'
import { Menu } from '../../ui/Menu'
import { uploadFile } from '../Chat'
import { admin, dropUpload } from '../admin'
import { Category, DISCORD_SETTINGS_URL, LinkRow, LoadError, Row, useLoad, useRemembered } from './parts'
import { itemsOf, KINDS, PlateRow, useMine, useShop, useWorn, wear, type Kind } from '../ShopPage'
import { lookOf } from '../collectibles'
import { ProfileCard, type Loaded } from '../profileParts'
import { CustomStatusLine } from '../presence'
import { Choice } from '../../settings/Choice'
import { Dialog } from '../settingsAccess'
import { EmojiField } from '../settingsGuide'
import { Spinner } from '../../ui/Spinner'
import { Skeleton } from '../../ui/Skeleton'

type Status = 'online' | 'idle' | 'dnd' | 'invisible'

const STATUSES: { id: Status; label: string }[] = [
  { id: 'online', label: 'Online' },
  { id: 'idle', label: 'Idle' },
  { id: 'dnd', label: 'Do not disturb' },
  { id: 'invisible', label: 'Invisible' },
]

/** The account from Discord (users/@me and its profile); null until it first arrives, then read again behind it. */
export function useAccount() {
  const [me, setMe] = useRemembered<DiscordAccount>('me')
  const [error, setError] = useState<string | null>(null)
  const [round, setRound] = useState(0)
  useEffect(() => {
    let live = true
    host.discordAccount('me').then(result => {
      if (!live) return
      if (result.ok && result.me) setMe(result.me)
      else setError(result.message ?? "Discord didn't send your account. Try again.")
    }).catch(() => { if (live) setError("Discord didn't send your account. Try again.") })
    return () => { live = false }
  }, [round, setMe])
  return { me, error, reload: () => { setError(null); setRound(r => r + 1) } }
}

function hex(color: number | null) {
  return color === null ? null : `#${color.toString(16).padStart(6, '0')}`
}

/** The top of Account: your profile card as others see it (decoration, effect, colors and all), and Edit User Profile. */
function AccountCard({ home, onEdit }: { home: DiscordHome['me']; onEdit: () => void }) {
  const mine = useMine(home)
  if (!mine) return null
  return (
    <div className="exo-dc-settings-profile">
      <ProfileCard data={mine} />
      <button type="button" className="exo-set-btn" onClick={onEdit}>Edit user profile</button>
    </div>
  )
}

/** Discord settings > Account: who you are, how you sign in, the devices signed in, and closing the account (on Discord's site). */
export function AccountPage({ home, onProfiles, onDevices }: { home: DiscordHome['me']; onProfiles: () => void; onDevices: () => void }) {
  const { me, error, reload } = useAccount()
  const hides = useStreamerHides()
  return (
    <>
      <h1>Account</h1>
      {/* Your card beside who you are, so the page's top row is filled, not a card and a button with space after them. */}
      <div className="exo-dc-account-top">
      <AccountCard home={home} onEdit={onProfiles} />
      <div>
      {me ? null : error ? <LoadError error={error} onRetry={reload} /> : <Skeleton label="Loading your account" />}
      {me ? (
        <Category title="Account info">
          <Row label="Display name" detail={me.globalName ?? "You haven't set a display name yet."}>
            <button type="button" className="exo-set-btn" onClick={onProfiles}>Edit</button>
          </Row>
          <Row label="Username" detail={me.username ?? ''}>
            <button type="button" className="exo-set-btn" onClick={() => void host.openUrl(DISCORD_SETTINGS_URL).catch(() => { })}>Edit</button>
          </Row>
          <LinkRow label="Email" detail={hides.personal ? 'Hidden in Streamer Mode' : me.email ?? "You haven't added an email yet."}
            button={me.email ? 'Edit' : 'Add'} url={DISCORD_SETTINGS_URL} />
          <LinkRow label="Phone number" detail={hides.personal ? 'Hidden in Streamer Mode' : me.phone ?? "You haven't added a phone number yet."}
            button={me.phone ? 'Edit' : 'Add'} url={DISCORD_SETTINGS_URL} />
        </Category>
      ) : null}
      </div>
      </div>
      <StatusRow home={home} />
      <Category title="Password & security">
        <LinkRow label="Password" button="Edit" url={DISCORD_SETTINGS_URL} />
        {me ? <LinkRow label="Multi-factor authentication" detail={me.mfa ? 'Enabled' : 'Set up'} button={me.mfa ? 'Manage' : 'Set up'} url={DISCORD_SETTINGS_URL} /> : null}
        <Row label="Logged-in devices" detail="These are all the devices that are currently logged-in with your Discord account.">
          <button type="button" className="exo-set-btn" onClick={onDevices}>View</button>
        </Row>
      </Category>
      <Category>
        <LinkRow label="Disable your account" detail="Temporarily disable your account." button="Disable account" url={DISCORD_SETTINGS_URL} danger />
        <LinkRow label="Close your account" detail="Permanently close your account." button="Delete account" url={DISCORD_SETTINGS_URL} danger />
      </Category>
    </>
  )
}

function StatusRow({ home }: { home: DiscordHome['me'] }) {
  const [status, setStatus] = useState<Status>(() => (STATUSES.some(s => s.id === home?.status) ? home?.status : 'online') as Status)
  const [error, setError] = useState<string | null>(null)
  // On screen at once; back as it was, with Discord's reason, when it didn't take.
  const change = (next: Status) => {
    const before = status
    setStatus(next)
    setError(null)
    const failed = (why?: string | null) => { setStatus(before); setError(why ?? "Discord didn't change your status. Try again.") }
    void host.discordSetStatus(next).then(result => { if (!result.ok) failed(result.message) }, () => failed())
  }
  return (
    <Category>
      <Row label="Status" detail="What your friends see." error={error}>
        <Menu label="Status" anchor="--exo-dc-status" offset={4} triggerClassName="exo-shelf-sort-trigger"
          trigger={STATUSES.find(s => s.id === status)?.label ?? 'Online'} items={STATUSES} checked={status}
          onAction={id => change(id as Status)} />
      </Row>
      <CustomStatusRow />
    </Category>
  )
}

// Discord's Clear after: in 24 hours, 4 hours, 1 hour or 30 minutes, or never.
const CLEAR_AFTER = [
  { value: '86400000', label: '24 hours' },
  { value: '14400000', label: '4 hours' },
  { value: '3600000', label: '1 hour' },
  { value: '1800000', label: '30 minutes' },
  { value: 'never', label: "Don't clear" },
]

// Discord's Set your status: a custom status's emoji and words (128), and when it clears; Clear status takes it away.
function CustomStatusRow() {
  const custom = useAccountSettings().customStatus
  const [open, setOpen] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const save = (next: CustomStatus | null) => { setError(null); void changeAccountSettings({ customStatus: next }).then(setError) }
  return (
    <Row label="Custom status" detail={custom ? <CustomStatusLine custom={custom} clears /> : "Let friends know what you're up to."} error={error}>
      <span className="exo-dc-settings-buttons">
        {custom ? <button type="button" className="exo-set-btn is-compact" onClick={() => save(null)}>Clear status</button> : null}
        <button type="button" className="exo-set-btn" onClick={() => setOpen(true)}>{custom ? 'Edit custom status' : 'Set a custom status'}</button>
      </span>
      {open ? <CustomStatusDialog custom={custom} onSave={save} onClose={() => setOpen(false)} /> : null}
    </Row>
  )
}

function CustomStatusDialog({ custom, onSave, onClose }: { custom: CustomStatus | null; onSave: (next: CustomStatus | null) => void; onClose: () => void }) {
  const [text, setText] = useState(custom?.text ?? '')
  const [emoji, setEmoji] = useState<{ id?: string | null; name?: string | null } | null>(custom && (custom.emojiId || custom.emojiName) ? { id: custom.emojiId, name: custom.emojiName } : null)
  const [clear, setClear] = useState(custom && !custom.expiresAtMs ? 'never' : CLEAR_AFTER[0].value)
  function save() {
    const words = text.trim()
    onSave(words || emoji?.name ? { text: words, emojiId: emoji?.id ?? null, emojiName: emoji?.name ?? null, expiresAtMs: clear === 'never' ? null : Date.now() + Number(clear) } : null)
    onClose()
  }
  return (
    <Dialog title="Set your status" onClose={onClose} actions={<button type="button" className="exo-set-btn is-primary" onClick={save}>Save</button>}>
      <span className="exo-dc-settings-buttons exo-dc-custom-status">
        <EmojiField emoji={emoji} onChange={setEmoji} />
        <input className="exo-dc-modal-input" value={text} maxLength={128} autoFocus placeholder="What's cookin'?" aria-label="Status"
          onChange={event => setText(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') save() }} />
      </span>
      <span className="exo-dc-settings-buttons">
        <span>Clear after</span>
        <Choice label="Clear after" value={clear} choices={CLEAR_AFTER} onChange={setClear} />
      </span>
    </Dialog>
  )
}

// "less than an hour ago", "3 hours ago", "2 days ago": when a device was last used, Discord's way.
function lastActive(iso: string | null) {
  if (!iso) return null
  const hours = (Date.now() - Date.parse(iso)) / 3_600_000
  if (!Number.isFinite(hours)) return null
  if (hours < 1) return 'less than an hour ago'
  if (hours < 24) return `${Math.round(hours)} hour${Math.round(hours) === 1 ? '' : 's'} ago`
  const days = Math.round(hours / 24)
  return days < 30 ? `${days} day${days === 1 ? '' : 's'} ago` : `${Math.round(days / 30)} month${Math.round(days / 30) === 1 ? '' : 's'} ago`
}

function Device({ session, busy, onLogOut }: { session: DiscordSession; busy?: boolean; onLogOut?: () => void }) {
  const hides = useStreamerHides()
  const line = [hides.personal ? null : session.location, session.current ? null : lastActive(session.lastUsed)].filter(Boolean).join(' · ')
  return (
    <Row label={session.os || 'Unknown'} detail={[session.platform, line].filter(Boolean).join(' · ')}>
      {onLogOut ? <button type="button" className="exo-set-btn" aria-label="Log out of device" disabled={busy} onClick={onLogOut}>Log out</button> : null}
    </Row>
  )
}

/** Discord settings > Account > Logged-in Devices: this device, the others, and logging them out. */
export function DevicesPage({ onBack }: { onBack: () => void }) {
  const { value, error, loading, reload } = useLoad('sessions', () => host.discordAccount('sessions'), data => data as DiscordSession[])
  const [failed, setFailed] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const current = value?.find(s => s.current)
  const others = (value ?? []).filter(s => !s.current).sort((a, b) => Date.parse(b.lastUsed ?? '') - Date.parse(a.lastUsed ?? ''))
  const logOut = (hashes: string[]) => {
    if (busy) return
    setFailed(null); setBusy(true)
    const why = "Discord didn't log that device out."
    void host.discordAccount('logoutSessions', { idHashes: hashes })
      .then(result => { if (result.ok) reload(); else setFailed(result.message ?? why) }, () => setFailed(why))
      .finally(() => setBusy(false))
  }
  return (
    <>
      <button type="button" className="exo-dc-settings-back" onClick={onBack}>Account</button>
      <h1>Logged-in devices</h1>
      <p className="exo-dc-settings-lede">These are all the devices that are currently logged-in with your Discord account. Log out of devices that you don&apos;t recognize.</p>
      {loading ? <Skeleton label="Loading devices" /> : null}
      {error ? <p className="exo-dc-settings-note" role="alert">{error}</p> : null}
      {failed ? <p className="exo-dc-settings-note" role="alert">{failed}</p> : null}
      {current ? <Category title="Current device"><Device session={current} /></Category> : null}
      {others.length ? (
        <Category title="Other devices">
          {others.map(s => <Device key={s.idHash} session={s} busy={busy} onLogOut={() => s.idHash && logOut([s.idHash])} />)}
        </Category>
      ) : null}
      {others.length ? (
        <Category>
          <Row label="Log out of all known devices" detail="You'll have to log back in on all logged out devices.">
            <button type="button" className="exo-set-btn is-danger" disabled={busy} onClick={() => logOut(others.map(s => s.idHash).filter((h): h is string => !!h))}>Log out all known devices</button>
          </Row>
        </Category>
      ) : null}
    </>
  )
}

type Picture = File | null | 'remove'
type Draft = { globalName: string; pronouns: string; bio: string; accentColor: number | null; avatar: Picture; banner: Picture }

function draftOf(me: DiscordAccount): Draft {
  return { globalName: me.globalName ?? '', pronouns: me.pronouns, bio: me.bio, accentColor: me.accentColor, avatar: null, banner: null }
}

// A new picture through discord.upload, or removing it, on the account's avatar or banner; null when it took.
async function savePicture(field: 'avatar' | 'banner', picture: Picture): Promise<string | null> {
  if (picture === 'remove') {
    const result = await host.discordAccount(field, { remove: true })
    return result.ok ? null : result.message ?? `Discord didn't remove your ${field}.`
  }
  if (!picture) return null
  const uploaded = await uploadFile(picture)
  if (typeof uploaded !== 'string') return uploaded.message
  const result = await host.discordAccount(field, { uploadId: uploaded })
  if (result.ok) return null
  dropUpload(uploaded)
  return result.message ?? "Discord didn't take that picture."
}

/** Saves what changed, the way Discord's Profiles does: the name and avatar on the account, the rest on the profile. */
async function saveProfile(me: DiscordAccount, draft: Draft): Promise<string | null> {
  if (draft.globalName !== (me.globalName ?? '')) {
    const result = await host.discordAccount('name', { globalName: draft.globalName })
    if (!result.ok) return result.message ?? "Discord didn't save your display name."
  }
  const profile: { bio?: string; pronouns?: string; accentColor?: number | null } = {}
  if (draft.bio !== me.bio) profile.bio = draft.bio
  if (draft.pronouns !== me.pronouns) profile.pronouns = draft.pronouns
  if (draft.accentColor !== me.accentColor) profile.accentColor = draft.accentColor
  if (Object.keys(profile).length) {
    const result = await host.discordAccount('profile', profile)
    if (!result.ok) return result.message ?? "Discord didn't save your profile."
  }
  return await savePicture('avatar', draft.avatar) ?? await savePicture('banner', draft.banner)
}

/** Discord settings > Profiles: User Profile and Server Profiles, as Discord's two tabs. */
export function ProfilesPage({ home, guilds }: { home: DiscordHome['me']; guilds: DiscordHome['guilds'] }) {
  const meId = home?.id ?? null
  const [tab, setTab] = useState<'user' | 'server'>('user')
  return (
    <>
      <h1>Profiles</h1>
      <div className="exo-dc-full-tabs" role="tablist">
        <button type="button" role="tab" aria-selected={tab === 'user'} onClick={() => setTab('user')}>Main profile</button>
        <button type="button" role="tab" aria-selected={tab === 'server'} onClick={() => setTab('server')}>Per-server profiles</button>
      </div>
      {tab === 'user' ? <UserProfile home={home} /> : <ServerProfiles meId={meId} guilds={guilds} />}
    </>
  )
}

const COLLECTIBLE_ROWS: [Kind, string][] = [['decoration', 'Avatar decoration'], ['effect', 'Profile effect'], ['nameplate', 'Nameplate']]

/** Your avatar decoration, profile effect and nameplate: put on one you own, or take it off, as Discord's profile settings do. */
function Collectibles({ meId }: { meId: string | null }) {
  const { shop, error, reload } = useShop()
  const { worn, put } = useWorn(meId)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<{ kind: Kind; text: string } | null>(null)
  const change = (kind: Kind, skuId: string | null) => {
    setBusy(true); setProblem(null)
    void wear(kind, skuId).then(text => {
      setBusy(false)
      if (text) setProblem({ kind, text })
      else put(kind, skuId)
    })
  }
  return (
    <Category title="Collectibles" detail="Get more in the shop, under friends.">
      <LoadError error={shop ? null : error} onRetry={reload} />
      {COLLECTIBLE_ROWS.map(([kind, label]) => {
        const owned = (shop?.owned ?? []).flatMap(product => itemsOf(product).filter(item => KINDS[item.type] === kind).map(item => ({ id: item.sku_id, label: product.name })))
        const current = owned.find(item => item.id === worn[kind])
        return (
          <Row key={kind} label={label} detail={current?.label ?? (worn[kind] ? 'On' : 'None')} error={problem?.kind === kind ? problem.text : null}>
            <span className="exo-dc-settings-buttons">
              {owned.length ? (
                <Menu label={`Change ${label}`} anchor={`--exo-dc-collectible-${kind}`} offset={4} triggerClassName="exo-set-btn" trigger={`Change ${label}`}
                  items={owned} checked={worn[kind] ?? ''} disabled={busy} onAction={id => change(kind, id)} />
              ) : null}
              {worn[kind] ? <button type="button" className="exo-set-btn" disabled={busy} onClick={() => change(kind, null)}>Remove</button> : null}
            </span>
          </Row>
        )
      })}
    </Category>
  )
}

type ServerNick = { guildId: string; saved: string; text: string; can: boolean }

/** Server Profiles: your nickname on the server you pick (Discord's per-server picture, banner and About Me need Nitro). */
function ServerProfiles({ meId, guilds }: { meId: string | null; guilds: DiscordHome['guilds'] }) {
  const [guildId, setGuildId] = useState(guilds[0]?.id ?? null)
  const [nick, setNick] = useState<ServerNick | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [round, setRound] = useState(0)
  useEffect(() => {
    if (!guildId || !meId) return
    let live = true
    void admin<{ ok: boolean; nick: string | null; can: { nick: boolean } }>('person', { guildId, userId: meId }).then(result => {
      if (!live) return
      if (result.ok && result.data?.ok) { setError(null); setNick({ guildId, saved: result.data.nick ?? '', text: result.data.nick ?? '', can: result.data.can.nick }) }
      else setError(result.message ?? "Discord didn't send your server profile. Try again.")
    })
    return () => { live = false }
  }, [guildId, meId, round])
  if (!guilds.length || !meId) return <p className="exo-dc-settings-note">You aren&apos;t in any servers.</p>
  const current = nick?.guildId === guildId ? nick : null
  const save = () => {
    if (!current || busy) return
    setBusy(true); setError(null)
    void admin('nick', { guildId: current.guildId, userId: meId, nick: current.text.trim() }).then(result => {
      setBusy(false)
      if (result.ok) setNick({ ...current, saved: current.text.trim(), text: current.text.trim() })
      else setError(result.message ?? "Discord didn't save your nickname.")
    })
  }
  return (
    <Category>
      <Row label="Choose a server">
        <Menu label="Server" anchor="--exo-dc-server-profile" offset={4} triggerClassName="exo-shelf-sort-trigger"
          trigger={guilds.find(g => g.id === guildId)?.name ?? 'Server'} items={guilds.map(g => ({ id: g.id, label: g.name }))} checked={guildId ?? ''}
          onAction={id => { setError(null); setGuildId(id) }} />
      </Row>
      <LoadError error={current ? null : error} onRetry={() => setRound(r => r + 1)} />
      {current ? (
        <Row label="Server nickname" detail={current.can ? undefined : "You can't change your nickname on this server."} error={error}>
          <span className="exo-dc-settings-buttons">
            <input className="exo-field exo-dc-settings-input" value={current.text} maxLength={32} disabled={!current.can} placeholder="No nickname" aria-label="Server nickname"
              onChange={e => setNick({ ...current, text: e.target.value })} onKeyDown={e => { if (e.key === 'Enter') save() }} />
            {current.text.trim() !== current.saved ? <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={save}>
              {busy ? <Spinner label="Saving your nickname" /> : null}Save
            </button> : null}
          </span>
        </Row>
      ) : null}
    </Category>
  )
}

/** User Profile: display name, pronouns, avatar, banner color and About Me, saved together, beside your card as it will look; then your collectibles. */
function UserProfile({ home }: { home: DiscordHome['me'] }) {
  const { me, error, reload } = useAccount()
  const [draft, setDraft] = useState<Draft | null>(null)
  // The account the draft was made from: when a newer one arrives (after a save) and nothing was edited meanwhile, the draft follows it.
  const [base, setBase] = useState<DiscordAccount | null>(null)
  const [saving, setSaving] = useState(false)
  const [failed, setFailed] = useState<string | null>(null)
  if (me && (!draft || (base && base !== me && JSON.stringify(draft) === JSON.stringify(draftOf(base))))) { setBase(me); setDraft(draftOf(me)) }
  if (!me || !draft) return error ? <LoadError error={error} onRetry={reload} /> : <Skeleton label="Loading your profile" />
  const changed = JSON.stringify(draft) !== JSON.stringify(draftOf(me))
  const set = (change: Partial<Draft>) => { setFailed(null); setDraft(d => d && { ...d, ...change }) }
  const save = () => {
    setSaving(true)
    void saveProfile(me, draft).then(problem => {
      setSaving(false)
      setFailed(problem)
      if (!problem) { setDraft(null); reload() }
    })
  }
  return (
    <>
      <div className="exo-dc-profile-editor">
        <div className="exo-dc-profile-fields">
          <ProfileFields draft={draft} me={me} onChange={set} />
          <Collectibles meId={home?.id ?? null} />
        </div>
        <DraftPreview home={home} draft={draft} />
      </div>
      {changed ? (
        <div className="exo-dc-settings-savebar exo-glass" role="region" aria-label="Unsaved changes">
          <span>Careful — you have unsaved changes!</span>
          {failed ? <em role="alert">{failed}</em> : null}
          <button type="button" className="exo-set-btn" disabled={saving} onClick={() => { setFailed(null); setDraft(draftOf(me)) }}>Reset</button>
          <button type="button" className="exo-set-btn is-primary" disabled={saving} onClick={save}>
            {saving ? <Spinner label="Saving your profile" /> : null}Save changes
          </button>
        </div>
      ) : null}
    </>
  )
}

// A picked picture shown before it's saved.
function usePicked(picture: Picture) {
  const [url, setUrl] = useState<string | null>(null)
  useEffect(() => {
    if (!(picture instanceof File)) { setUrl(null); return }
    const next = URL.createObjectURL(picture)
    setUrl(next)
    return () => URL.revokeObjectURL(next)
  }, [picture])
  return url
}

// Your profile with the unsaved changes in it.
function drafted(mine: Loaded, draft: Draft): Loaded {
  const { profile } = mine
  return {
    ...mine,
    profile: {
      ...profile,
      user: { ...profile.user, global_name: draft.globalName || null },
      user_profile: { ...profile.user_profile, bio: draft.bio, pronouns: draft.pronouns, accent_color: draft.accentColor },
    },
  }
}

/** Your card as it will look once saved: the real profile card (decoration, effect, colors), with what you changed. */
function DraftPreview({ home, draft }: { home: DiscordHome['me']; draft: Draft }) {
  const mine = useMine(home)
  const pickedAvatar = usePicked(draft.avatar)
  const pickedBanner = usePicked(draft.banner)
  if (!mine) return null
  const data = drafted(mine, draft)
  return (
    <aside className="exo-dc-profile-preview" aria-label="Preview">
      <h2>Preview</h2>
      <ProfileCard data={data} avatar={draft.avatar === 'remove' ? null : pickedAvatar ?? undefined} banner={draft.banner === 'remove' ? null : pickedBanner ?? undefined} />
      {lookOf(data.profile.user)?.nameplate ? <PlateRow data={data} /> : null}
    </aside>
  )
}

function ProfileFields({ draft, me, onChange }: { draft: Draft; me: DiscordAccount; onChange: (change: Partial<Draft>) => void }) {
  const { premium } = me
  const hasAvatar = !!me.avatar
  const file = useRef<HTMLInputElement>(null)
  const bannerFile = useRef<HTMLInputElement>(null)
  const hasBanner = draft.banner instanceof File || (!!me.banner && draft.banner !== 'remove')
  return (
    <Category>
      <Row label="Display name">
        <input className="exo-field exo-dc-settings-input" value={draft.globalName} maxLength={32} aria-label="Display name" onChange={e => onChange({ globalName: e.target.value })} />
      </Row>
      <Row label="Pronouns">
        <input className="exo-field exo-dc-settings-input" value={draft.pronouns} maxLength={40} placeholder="Add your pronouns" aria-label="Pronouns" onChange={e => onChange({ pronouns: e.target.value })} />
      </Row>
      <ServerTagRow />
      <Row label="Avatar" detail={premium ? 'PNG, JPEG, GIF or WebP, up to 10 MB.' : 'PNG, JPEG or WebP, up to 10 MB. Animated avatars need Nitro.'}>
        <span className="exo-dc-settings-buttons">
          <button type="button" className="exo-set-btn" onClick={() => file.current?.click()}>Change avatar</button>
          {hasAvatar || draft.avatar instanceof File ? <button type="button" className="exo-set-btn" onClick={() => onChange({ avatar: 'remove' })}>Remove avatar</button> : null}
        </span>
        <input ref={file} type="file" accept="image/png,image/jpeg,image/gif,image/webp" hidden
          onChange={e => { const picked = e.target.files?.[0]; e.target.value = ''; if (picked) onChange({ avatar: picked }) }} />
      </Row>
      <Row label="Profile banner" detail={premium ? 'PNG, JPEG, GIF or WebP, up to 10 MB. 600 × 240 or larger fits best.' : 'Banners need Nitro. Without it, your banner color below is what people see.'}>
        {premium || hasBanner ? (
          <span className="exo-dc-settings-buttons">
            {premium ? <button type="button" className="exo-set-btn" onClick={() => bannerFile.current?.click()}>Change banner</button> : null}
            {hasBanner ? <button type="button" className="exo-set-btn" onClick={() => onChange({ banner: me.banner ? 'remove' : null })}>Remove banner</button> : null}
          </span>
        ) : null}
        <input ref={bannerFile} type="file" accept="image/png,image/jpeg,image/gif,image/webp" hidden
          onChange={e => { const picked = e.target.files?.[0]; e.target.value = ''; if (picked) onChange({ banner: picked }) }} />
      </Row>
      <Row label="Banner color" detail="Shown when you have no banner picture.">
        <label className="exo-set-btn exo-dc-settings-swatch">
          <span style={{ background: hex(draft.accentColor) ?? 'var(--exo-selected)' }} aria-hidden="true" />
          {hex(draft.accentColor)?.toUpperCase() ?? 'Default'}
          <input type="color" aria-label="Banner color" value={hex(draft.accentColor) ?? '#262626'}
            onChange={e => onChange({ accentColor: parseInt(e.target.value.slice(1), 16) })} />
        </label>
      </Row>
      <div className="exo-dc-settings-row is-stacked">
        <span className="exo-dc-settings-copy"><strong>Bio</strong><span>Markdown and links work, as in messages.</span></span>
        <span className="exo-dc-settings-counted">
          <textarea className="exo-field exo-dc-settings-textarea" value={draft.bio} maxLength={190} rows={4} aria-label="Bio" onChange={e => onChange({ bio: e.target.value })} />
          <em>{draft.bio.length}/190</em>
        </span>
      </div>
    </Category>
  )
}

type TagServer = { id: string; name: string; tag: string; badge: string | null }

/**
 * Discord's Server Tag on your profile: wear the tag of a server you're in that has one (Discord shows it beside your
 * name everywhere), or none. Saved as you pick, as Discord's Profiles saves it.
 */
function ServerTagRow() {
  const [servers, setServers] = useState<TagServer[] | null>(null)
  const [mine, setMine] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let live = true
    void host.discordAccount('tags').then(result => {
      if (!live) return
      if (result.ok) { setServers(result.servers ?? []); setMine(result.mine ?? null) } else setError(result.message ?? "Discord didn't answer.")
    }).catch(() => { if (live) setError("Discord didn't answer.") })
    return () => { live = false }
  }, [])
  async function wear(guildId: string | null) {
    setBusy(true); setError(null)
    const result = await host.discordAccount('setTag', { guildId }).catch(() => ({ ok: false, message: "Discord didn't answer." }))
    setBusy(false)
    if (result.ok) setMine(guildId); else setError(result.message ?? 'Discord did not change your tag.')
  }
  const worn = servers?.find(s => s.id === mine)
  const detail = error ?? (servers && !servers.length ? 'None of your servers has a server tag yet.' : 'Show a server you’re in beside your name.')
  return (
    <Row label="Server tag" detail={detail}>
      <span className="exo-dc-settings-buttons">
        {servers?.length ? (
          <Menu label="Server tag" anchor="--exo-dc-server-tag" offset={4} triggerClassName="exo-shelf-sort-trigger" disabled={busy}
            trigger={worn ? `${worn.tag} · ${worn.name}` : 'No tag'} checked={mine ?? ''}
            items={servers.map(s => ({ id: s.id, label: `${s.tag} · ${s.name}` }))} onAction={id => void wear(id)} />
        ) : null}
        {mine ? <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void wear(null)}>Remove</button> : null}
      </span>
    </Row>
  )
}
