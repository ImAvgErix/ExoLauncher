import { useRef, useState } from 'react'
import { Close } from '../../brand/icons'
import { useGlide } from '../../lib/glide'
import type { DiscordChannel, DiscordModeration } from '../../lib/discord'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { admin, dropUpload, Field, SaveBar, pickFile, previewOf, ReadError, UnsavedProvider, useAdmin, useLayer, useUnsaved } from './admin'
import { uploadFile } from './Chat'
import { Roles } from './settingsRoles'
import { Emoji, Stickers } from './settingsExpressions'
import { AuditLog, Bans, Integrations, Invites } from './settingsModeration'
import { AutoMod } from './settingsAutomod'
import { SettingsLayer } from './SettingsLayer'
import { Members } from './serverPages'
import { pushPage } from '../../lib/viewTransition'

export type Guild = {
  id: string; name: string; icon: string | null; banner: string | null; description: string | null; owner_id: string
  afk_channel_id: string | null; afk_timeout: number; system_channel_id: string | null; system_channel_flags: number
  verification_level: number; default_message_notifications: number; explicit_content_filter: number; mfa_level: number
  premium_tier: number; features: string[]
}

type Section = 'profile' | 'engagement' | 'emoji' | 'stickers' | 'members' | 'roles' | 'invites' | 'integrations' | 'safety' | 'automod' | 'audit' | 'bans'
type Item = { id: Section; label: string; shown: (m: DiscordModeration) => boolean }

// Discord's Server Settings list: its groups and order, each section with the permission Discord asks for.
const NAV: { heading: string | null; items: Item[] }[] = [
  { heading: null, items: [{ id: 'profile', label: 'Server Profile', shown: () => true }, { id: 'engagement', label: 'Engagement', shown: () => true }] },
  { heading: 'Expression', items: [{ id: 'emoji', label: 'Emoji', shown: m => m.manageExpressions }, { id: 'stickers', label: 'Stickers', shown: m => m.manageExpressions }] },
  {
    heading: 'People', items: [
      { id: 'members', label: 'Members', shown: m => m.kick || m.ban || m.timeout || m.manageRoles || m.manageGuild },
      { id: 'roles', label: 'Roles', shown: m => m.manageRoles },
      { id: 'invites', label: 'Invites', shown: m => m.manageGuild },
    ],
  },
  { heading: 'Apps', items: [{ id: 'integrations', label: 'Integrations', shown: m => m.manageGuild || m.manageWebhooks }] },
  {
    heading: 'Moderation', items: [
      { id: 'safety', label: 'Safety Setup', shown: m => m.manageGuild },
      { id: 'automod', label: 'AutoMod', shown: m => m.manageGuild },
      { id: 'audit', label: 'Audit Log', shown: m => m.auditLog },
      { id: 'bans', label: 'Bans', shown: m => m.ban },
    ],
  },
]

/**
 * Discord's Server Settings in Exo's look: each section only with the permission Discord shows it for.
 * Members opens the server's Members page, as Discord does for a Community server; elsewhere it is a section here.
 * Unsaved changes hold the page: changing section, closing or Escape shakes the save bar instead.
 */
export function ServerSettings({ guildId, name, moderation, channels, onClose, onMembers }: {
  guildId: string
  name: string
  moderation: DiscordModeration
  channels: DiscordChannel[]
  onClose: () => void
  onMembers?: () => void
}) {
  const [section, setSection] = useState<Section>('profile')
  const glide = useGlide<HTMLDivElement>()
  const guild = useAdmin<Guild>('guild', { guildId })
  const unsaved = useUnsaved()
  const close = () => unsaved.guard(onClose)
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, close)
  const pick = (id: Section) => unsaved.guard(() => { if (id === 'members' && onMembers) { onClose(); onMembers() } else void pushPage(() => setSection(id), '.exo-dc-settings-page', '.exo-dc-settings-nav [aria-current="true"]') })
  return (
    <UnsavedProvider value={unsaved}>
    <SettingsLayer onClose={close}>
    <div ref={box} className="exo-dc-settings" role="dialog" aria-modal="true" aria-label={`${name} settings`} tabIndex={-1}>
      <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
      <nav className="exo-dc-settings-nav" aria-label="Server settings sections">
        <div ref={glide} className="exo-glide">
          {NAV.map(group => {
            const items = group.items.filter(item => item.shown(moderation))
            if (!items.length) return null
            return (
              <section key={group.heading ?? 'server'} aria-label={group.heading ?? name}>
                <h2>{group.heading ?? name}</h2>
                <ul>
                  {items.map(item => (
                    <li key={item.id}>
                      <button type="button" aria-current={section === item.id ? 'true' : undefined} className={section === item.id ? 'is-current' : undefined}
                        onClick={() => pick(item.id)}>{item.label}</button>
                    </li>
                  ))}
                </ul>
              </section>
            )
          })}
        </div>
      </nav>
      <div className="exo-dc-settings-body">
        <button type="button" className="exo-game-close exo-dc-settings-close" aria-label="Close server settings" onClick={close}><Close size={16} /></button>
        <div key={section} className="exo-dc-settings-page exo-motion-switch">
          <SectionPage section={section} guildId={guildId} moderation={moderation} channels={channels} guild={guild.data} error={guild.error} onSaved={guild.reload} />
        </div>
      </div>
    </div>
    </SettingsLayer>
    </UnsavedProvider>
  )
}

function SectionPage({ section, guildId, moderation, channels, guild, error, onSaved }: {
  section: Section
  guildId: string
  moderation: DiscordModeration
  channels: DiscordChannel[]
  guild: Guild | null
  error: string | null
  onSaved: () => void
}) {
  switch (section) {
    case 'roles': return <Roles guildId={guildId} />
    case 'emoji': return <Emoji guildId={guildId} tier={guild?.premium_tier ?? 0} />
    case 'stickers': return <Stickers guildId={guildId} tier={guild?.premium_tier ?? 0} />
    case 'invites': return <Invites guildId={guildId} />
    case 'integrations': return <Integrations guildId={guildId} moderation={moderation} channels={channels} />
    case 'automod': return <AutoMod guildId={guildId} channels={channels} />
    case 'audit': return <AuditLog guildId={guildId} />
    case 'bans': return <Bans guildId={guildId} />
    case 'members': return <><h1>Members</h1><Members guildId={guildId} canPrune={moderation.prune} /></>
    default: break
  }
  // A failed read keeps what the page had, with Try Again.
  if (!guild) return error ? <ReadError error={error} onRetry={onSaved} /> : <p className="exo-dc-note">Loading</p>
  return (
    <>
      <Overview key={section} section={section} guild={guild} canEdit={moderation.manageGuild} channels={channels} onSaved={onSaved} />
      <ReadError error={error} onRetry={onSaved} />
    </>
  )
}

type Draft = Partial<Pick<Guild, 'name' | 'description' | 'afk_channel_id' | 'afk_timeout' | 'system_channel_id' | 'system_channel_flags' | 'default_message_notifications'
  | 'verification_level' | 'explicit_content_filter'>>
type Picture = { file: File; preview: string } | null

// Server Profile, Engagement and Safety Setup: changes wait in a draft until Save Changes, as in Discord.
function Overview({ section, guild, canEdit, channels, onSaved }: { section: Section; guild: Guild; canEdit: boolean; channels: DiscordChannel[]; onSaved: () => void }) {
  const [draft, setDraft] = useState<Draft>({})
  const [icon, setIcon] = useState<Picture | undefined>(undefined)
  const [banner, setBanner] = useState<Picture | undefined>(undefined)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [nameError, setNameError] = useState<string | null>(null)
  const value = <K extends keyof Draft>(key: K) => (key in draft ? draft[key] : guild[key]) as Guild[K]
  const set = (change: Draft) => { setError(null); setNameError(null); setDraft(d => ({ ...d, ...change })) }
  const dirty = Object.keys(draft).length > 0 || icon !== undefined || banner !== undefined
  async function save() {
    // Discord's rule for a server name, said under Name rather than dropped.
    const name = draft.name?.trim()
    if (name !== undefined && (name.length < 2 || name.length > 100)) { setNameError('Server name must be between 2 and 100 in length.'); return }
    setBusy(true)
    const changes: Record<string, unknown> = { ...draft }
    // Pictures sent for a save that didn't happen are dropped, so they stop counting toward the upload limit.
    const sent: string[] = []
    for (const [key, picture] of [['icon', icon], ['banner', banner]] as const) {
      if (picture === undefined) continue
      if (picture === null) { changes[key] = null; continue }
      const uploaded = await uploadFile(picture.file)
      if (typeof uploaded !== 'string') { sent.forEach(dropUpload); setError(uploaded.message); setBusy(false); return }
      sent.push(uploaded)
      changes[key] = uploaded
    }
    const result = await admin('saveGuild', { guildId: guild.id, changes })
    setBusy(false)
    if (!result.ok) { sent.forEach(dropUpload); setError(result.message ?? 'Discord did not save that.'); return }
    setDraft({}); setIcon(undefined); setBanner(undefined); onSaved()
  }
  const reset = () => { setDraft({}); setIcon(undefined); setBanner(undefined); setError(null); setNameError(null) }
  return (
    <>
      {section === 'profile' ? <Profile guild={guild} canEdit={canEdit} value={value} set={set} nameError={nameError} icon={icon} setIcon={setIcon} banner={banner} setBanner={setBanner} /> : null}
      {section === 'engagement' ? <Engagement canEdit={canEdit} value={value} set={set} channels={channels} /> : null}
      {section === 'safety' ? <Safety guild={guild} value={value} set={set} /> : null}
      <SaveBar shown={dirty} busy={busy} error={error} onReset={reset} onSave={() => void save()} />
    </>
  )
}

type Value = <K extends keyof Draft>(key: K) => Guild[K]

function Profile({ guild, canEdit, value, set, nameError, icon, setIcon, banner, setBanner }: {
  guild: Guild
  canEdit: boolean
  value: Value
  set: (change: Draft) => void
  nameError: string | null
  icon: Picture | undefined
  setIcon: (picture: Picture | undefined) => void
  banner: Picture | undefined
  setBanner: (picture: Picture | undefined) => void
}) {
  const iconUrl = icon === undefined ? (guild.icon ? `https://cdn.discordapp.com/icons/${guild.id}/${guild.icon}.webp?size=256&quality=lossless` : null) : icon?.preview ?? null
  const bannerUrl = banner === undefined ? (guild.banner ? `https://cdn.discordapp.com/banners/${guild.id}/${guild.banner}.webp?size=600` : null) : banner?.preview ?? null
  const choose = async (apply: (picture: Picture) => void) => {
    const file = await pickFile('image/png,image/jpeg,image/gif,image/webp')
    if (file) apply({ file, preview: await previewOf(file) })
  }
  return (
    <>
      <h1>Server Profile</h1>
      <Field label="Name" error={nameError}>
        <input className="exo-dc-modal-input exo-dc-field" value={value('name')} maxLength={100} disabled={!canEdit} aria-label="Server name"
          onChange={event => set({ name: event.target.value })} />
      </Field>
      <Field label="Icon" detail="We recommend an image of at least 512x512.">
        <span className="exo-dc-picture">
          {iconUrl ? <img src={iconUrl} alt="" draggable={false} /> : <span className="exo-dc-picture-empty">{guild.name.slice(0, 1)}</span>}
          {canEdit ? <button type="button" className="exo-dc-tab is-raised" onClick={() => void choose(setIcon)}>Change Icon</button> : null}
          {canEdit && iconUrl ? <button type="button" className="exo-dc-tab" onClick={() => setIcon(null)}>Remove Icon</button> : null}
        </span>
      </Field>
      {guild.features.includes('BANNER') ? (
        <Field label="Banner" detail="Shown at the top of your channel list. At least 960x540, 16:9.">
          <span className="exo-dc-picture is-banner">
            {bannerUrl ? <img src={bannerUrl} alt="" draggable={false} /> : null}
            {canEdit ? <button type="button" className="exo-dc-tab is-raised" onClick={() => void choose(setBanner)}>Change Banner</button> : null}
            {canEdit && bannerUrl ? <button type="button" className="exo-dc-tab" onClick={() => setBanner(null)}>Remove</button> : null}
          </span>
        </Field>
      ) : null}
      {guild.features.includes('COMMUNITY') ? (
        <Field label="Description" detail="How did your server get started? Why should people join?" stacked>
          <textarea className="exo-dc-modal-input exo-dc-field is-wide" rows={3} maxLength={120} value={value('description') ?? ''} disabled={!canEdit} aria-label="Server description"
            onChange={event => set({ description: event.target.value || null })} />
        </Field>
      ) : null}
    </>
  )
}

const AFK = [{ value: '60', label: '1 minute' }, { value: '300', label: '5 minutes' }, { value: '900', label: '15 minutes' }, { value: '1800', label: '30 minutes' }, { value: '3600', label: '1 hour' }]
// System channel flags (Discord's SUPPRESS_* bits), each switch on while its bit is off.
const SYSTEM_FLAGS = [
  { bit: 1, label: 'Send a random welcome message when someone joins this server.' },
  { bit: 8, label: 'Prompt members to reply to welcome messages with a sticker.' },
  { bit: 2, label: 'Send a message when someone boosts this server.' },
  { bit: 4, label: 'Send helpful tips for server setup.' },
]

function Engagement({ canEdit, value, set, channels }: { canEdit: boolean; value: Value; set: (change: Draft) => void; channels: DiscordChannel[] }) {
  const text = channels.filter(c => c.kind === 'text' && c.type === 0)
  const voice = channels.filter(c => c.kind === 'voice' && c.type === 2)
  const system = value('system_channel_id')
  const flags = value('system_channel_flags')
  const notify = value('default_message_notifications')
  return (
    <>
      <h1>Engagement</h1>
      <Field label="System Messages Channel" detail="This is the channel we send system event messages to.">
        <Choice label="System Messages Channel" value={system ?? 'none'} disabled={!canEdit}
          choices={[{ value: 'none', label: 'No System Messages' }, ...text.map(c => ({ value: c.id, label: `#${c.name}` }))]}
          onChange={id => set({ system_channel_id: id === 'none' ? null : id })} />
      </Field>
      {SYSTEM_FLAGS.map(flag => (
        <Field key={flag.bit} label={flag.label}>
          <Switch checked={(flags & flag.bit) === 0} disabled={!canEdit || !system} label={flag.label}
            onChange={on => set({ system_channel_flags: on ? flags & ~flag.bit : flags | flag.bit })} />
        </Field>
      ))}
      <Field label="Inactive Channel" detail="Automatically move members to this channel and mute them when they have been idle for longer than the inactive time-out. This does not affect browsers.">
        <Choice label="Inactive Channel" value={value('afk_channel_id') ?? 'none'} disabled={!canEdit}
          choices={[{ value: 'none', label: 'No Inactive Channel' }, ...voice.map(c => ({ value: c.id, label: c.name }))]}
          onChange={id => set({ afk_channel_id: id === 'none' ? null : id })} />
      </Field>
      <Field label="Inactive Timeout">
        <Choice label="Inactive Timeout" value={String(value('afk_timeout'))} disabled={!canEdit || !value('afk_channel_id')} choices={AFK}
          onChange={seconds => set({ afk_timeout: Number(seconds) })} />
      </Field>
      <Field label="Default Notification Settings" detail="This will determine whether members who have not explicitly set their notification settings receive a notification for every message sent in this server or not.">
        <div className="exo-shelf-chips" role="radiogroup" aria-label="Default Notification Settings">
          {[{ v: 0, label: 'All Messages' }, { v: 1, label: 'Only @mentions' }].map(option => (
            <button key={option.v} type="button" role="radio" className="exo-shelf-chip" disabled={!canEdit} aria-checked={notify === option.v} aria-pressed={notify === option.v}
              onClick={() => set({ default_message_notifications: option.v })}>{option.label}</button>
          ))}
        </div>
      </Field>
    </>
  )
}

const VERIFICATION = [
  { level: 0, name: 'None', detail: 'Unrestricted' },
  { level: 1, name: 'Low', detail: 'Must have a verified email on their Discord account.' },
  { level: 2, name: 'Medium', detail: 'Must also be registered on Discord for longer than 5 minutes.' },
  { level: 3, name: 'High', detail: 'Must also be a member of this server for longer than 10 minutes.' },
  { level: 4, name: 'Highest', detail: 'Must have a verified phone on their Discord account.' },
]
const CONTENT_FILTER = [
  { level: 2, name: 'Filter messages from all members', detail: 'All messages will be filtered for sensitive image-based media.' },
  { level: 1, name: 'Filter messages from server members without roles', detail: 'Messages from server members without a role will be filtered for sensitive image-based media.' },
  { level: 0, name: 'Do not filter', detail: 'Messages will not be filtered for sensitive image-based media.' },
]

// Discord's Safety Setup: verification level and the sensitive content filter, saved with Save Changes; the 2FA requirement shows as set.
function Safety({ guild, value, set }: { guild: Guild; value: Value; set: (change: Draft) => void }) {
  const community = guild.features.includes('COMMUNITY')
  return (
    <>
      <h1>Safety Setup</h1>
      <Field label="Verification Level" detail="Members of the server must meet the following criteria before they can send messages in text channels or initiate a direct message conversation. If a member has an assigned role and server onboarding is not enabled, this does not apply." stacked>
        <Options options={VERIFICATION.map(v => ({ ...v, disabled: community && v.level === 0, detail: community && v.level === 0 ? 'This option is not available for Community Servers.' : v.detail }))}
          value={value('verification_level')} label="Verification Level" onPick={level => set({ verification_level: level })} />
      </Field>
      <Field label="Sensitive content filters" detail="Choose if server members can share image-based media detected by Discord’s sensitive content filters." stacked>
        <Options options={CONTENT_FILTER.map(v => ({ ...v, disabled: community && v.level !== 2, detail: community && v.level !== 2 ? 'This option is not available for Community Servers.' : v.detail }))}
          value={value('explicit_content_filter')} label="Sensitive content filters" onPick={level => set({ explicit_content_filter: level })} />
      </Field>
      <Field label="Require 2FA for moderator actions" detail="Moderators must have two-factor authentication enabled to ban, kick or time-out members and delete messages. Only the server owner can change this setting if they have 2FA enabled.">
        <Switch checked={guild.mfa_level === 1} disabled label="Require 2FA for moderator actions" onChange={() => { }} />
      </Field>
    </>
  )
}

function Options({ options, value, label, onPick }: { options: { level: number; name: string; detail: string; disabled?: boolean }[]; value: number; label: string; onPick: (level: number) => void }) {
  return (
    <div className="exo-dc-options" role="radiogroup" aria-label={label}>
      {options.map(option => (
        <button key={option.level} type="button" role="radio" aria-checked={value === option.level} disabled={option.disabled}
          className={`exo-dc-option${value === option.level ? ' is-current' : ''}`} onClick={() => { if (value !== option.level) onPick(option.level) }}>
          <span className="exo-dc-option-dot" aria-hidden="true" />
          <span className="exo-dc-settings-copy"><strong>{option.name}</strong><span>{option.detail}</span></span>
        </button>
      ))}
    </div>
  )
}
