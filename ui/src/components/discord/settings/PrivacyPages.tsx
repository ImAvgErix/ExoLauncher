import { useState } from 'react'
import { host } from '../../../lib/host'
import type { DiscordHome } from '../../../lib/discord'
import { changeAccountSettings, useAccountSettings, useStreamerHides, type AccountSettings, type DiscordAuthorizedApp, type DiscordConnection } from '../../../lib/discordPrefs'
import { connectionLabel } from '../profileParts'
import { Confirm } from '../admin'
import { AccountToggle, Category, ChoiceRow, LinkRow, LoadError, Row, ToggleRow, useLoad } from './parts'
import { useAccount } from './AccountPages'

const REDACTION = { show: 1, blur: 2, block: 3 } as const

// Discord's own defaults for an unset filter: shown to adults among friends and in servers, blocked from others; blurred for teens.
function redaction(value: number, adult: boolean, dm: boolean, friend: boolean) {
  if (value !== 0) return value
  if (!adult) return dm && !friend ? REDACTION.block : REDACTION.blur
  return dm && !friend ? REDACTION.block : REDACTION.show
}

type FilterKind = 'explicit' | 'gore'

function FilterSet({ kind, settings, adult }: { kind: FilterKind; settings: AccountSettings; adult: boolean }) {
  const field = (where: 'FriendDm' | 'NonFriendDm' | 'Guilds') => `${kind}Content${where}` as const
  const dm = [...(adult ? [{ value: REDACTION.show as number, label: 'Show' }] : []), { value: REDACTION.blur, label: 'Blur' }, { value: REDACTION.block, label: 'Block' }]
  const servers = [...(adult ? [{ value: REDACTION.show as number, label: 'Show' }] : []), { value: REDACTION.blur, label: 'Blur' }]
  const save = (key: ReturnType<typeof field>) => (value: number) => changeAccountSettings({ [key]: value })
  return (
    <Category title={kind === 'explicit' ? 'Mature Sexual Media' : 'Graphic Media'}
      detail={kind === 'explicit'
        ? 'Helps detect image-based media that may contain sexually explicit or suggestive material involving adults.'
        : 'Helps detect image-based media that may contain violent or potentially disturbing visual content.'}>
      <ChoiceRow label="Direct messages from friends" value={redaction(settings[field('FriendDm')], adult, true, true)} choices={dm} onChange={save(field('FriendDm'))} />
      <ChoiceRow label="Direct messages from others" value={redaction(settings[field('NonFriendDm')], adult, true, false)} choices={dm} onChange={save(field('NonFriendDm'))} />
      <ChoiceRow label="Messages in server channels" value={redaction(settings[field('Guilds')], adult, false, false)} choices={servers} disabled={!adult} onChange={save(field('Guilds'))} />
    </Category>
  )
}

// The spam filter as Discord shows it: dm_spam_filter_v2, else what the older setting said.
function spamFilter(settings: AccountSettings, adult: boolean) {
  if (settings.dmSpamFilterV2 !== 0) return settings.dmSpamFilterV2
  if (!adult) return 3
  return [1, 2, 3][settings.explicitContentFilter] ?? 2
}

/** Discord settings > Messaging Permissions: content filters, spam, who may DM you and send friend requests. */
export function MessagingPage({ guilds }: { guilds: DiscordHome['guilds'] }) {
  const settings = useAccountSettings()
  const adult = useAdult()
  const [askAll, setAskAll] = useState<null | { kind: 'dms' | 'requests'; restricted: boolean }>(null)
  const [dmError, setDmError] = useState<string | null>(null)
  const dmsRestricted = settings.defaultGuildsRestrictedV2 ?? settings.defaultGuildsRestricted
  const flags = settings.friendSourceFlags
  const everyone = (flags & 8) !== 0
  const setFlag = (bit: number, on: boolean) => changeAccountSettings({ friendSourceFlags: on ? flags | bit : flags & ~bit & ~8 })
  const applyDefault = (all: boolean) => {
    if (!askAll) return
    const ids = all ? (askAll.restricted ? guilds.map(g => g.id) : []) : undefined
    setDmError(null)
    void changeAccountSettings(askAll.kind === 'dms'
      ? { defaultGuildsRestrictedV2: askAll.restricted, ...(ids ? { restrictedGuildIds: ids } : {}) }
      : { defaultMessageRequestRestricted: askAll.restricted, ...(ids ? { messageRequestRestrictedGuildIds: ids } : {}) }).then(setDmError)
    setAskAll(null)
  }
  return (
    <>
      <h1>Messaging Permissions</h1>
      <Category title="Content Filters">
        <FilterSet kind="explicit" settings={settings} adult={adult} />
        <FilterSet kind="gore" settings={settings} adult={adult} />
        <AccountToggle label="Allow access to age-restricted commands from apps in DMs" detail="Allows people 18+ to access commands marked as age-restricted in DMs. Applies to all apps."
          settings={settings} field="viewNsfwCommands" disabled={!adult} />
        <AccountToggle label="Allow access to age-restricted servers on iOS" detail="Access age-restricted servers (18+) on iOS devices, after joining them on desktop."
          settings={settings} field="viewNsfwGuilds" disabled={!adult} />
      </Category>
      <Category title="Spam Filters">
        <ChoiceRow label="Automatically filter suspected spam messages" detail="Discord can filter out some messages that contain spam. These messages go to your Spam inbox."
          value={spamFilter(settings, adult)} onChange={value => changeAccountSettings({ dmSpamFilterV2: value })}
          choices={[{ value: 3, label: 'Filter all spam' }, { value: 2, label: 'Filter messages from non-friends (Recommended)' }, { value: 1, label: "Don't filter spam" }]} />
      </Category>
      <Category title="Direct Message (DM) Permissions">
        <ToggleRow label="Direct Messages" detail="Allow DMs from other server members" checked={!dmsRestricted}
          onChange={value => { setAskAll({ kind: 'dms', restricted: !value }) }} />
        <ToggleRow label="Message requests" detail="Filter messages from server members you may not know." checked={!dmsRestricted && !settings.defaultMessageRequestRestricted}
          disabled={dmsRestricted} onChange={value => { setAskAll({ kind: 'requests', restricted: !value }) }} />
        {askAll ? (
          <div className="exo-dc-settings-ask exo-glass" role="alertdialog" aria-label={askAll.kind === 'dms' ? 'Server Privacy' : 'Message Requests'}>
            <span>Do you want to also apply this change to all your existing servers?</span>
            <button type="button" className="exo-set-btn" onClick={() => applyDefault(false)}>No</button>
            <button type="button" className="exo-set-btn is-primary" onClick={() => applyDefault(true)}>Yes</button>
          </div>
        ) : null}
        {dmError ? <p className="exo-dc-settings-note" role="alert">{dmError}</p> : null}
      </Category>
      <Category title="Friend Request Permissions">
        <ToggleRow label="Everyone" checked={everyone} onChange={value => changeAccountSettings({ friendSourceFlags: value ? 14 : 14 & ~8 })} />
        <ToggleRow label="Friend of friends" checked={everyone || (flags & 2) !== 0} disabled={everyone} onChange={value => setFlag(2, value)} />
        <ToggleRow label="Server members" detail="Server members can only send you friend requests from servers where you also allow Direct Messages."
          checked={everyone || (flags & 4) !== 0} disabled={everyone} onChange={value => setFlag(4, value)} />
        <AccountToggle label="Show personalized messages" detail="Show personalized messages on incoming friend requests. If you accept, the message will still appear in your DMs."
          settings={settings} field="hideFriendRequestNotes" invert />
      </Category>
      <Category title="Messaging in Connected Games">
        <AccountToggle label="Allow friends from games to send direct messages and invites" detail="Let friends from connected games send DMs and invite me to play games, even when the game we play isn't open."
          settings={settings} field="allowGameFriendDmsInDiscord" />
      </Category>
    </>
  )
}

/** Whether the account may see age-restricted content (Discord's nsfw_allowed): adults get Show among the filters. */
function useAdult() {
  return useAccount().me?.nsfwAllowed ?? true
}

type Consent = 'usage_statistics' | 'personalization'
// Discord's question before each consent is turned off.
const TURN_OFF: Record<Consent, { title: string; body: string }> = {
  usage_statistics: {
    title: 'Turn off use data to improve Discord?',
    body: "If you turn this off, we'll stop processing new data for analytics. You will lose access to new experimental features and usage-based improvements to features.",
  },
  personalization: {
    title: 'Turn off personalization?',
    body: "By turning this off you lose access to things like personalized recommendations and suggestions. You won't be able to recover them in the future!",
  },
}

/** Discord settings > Data & Privacy: how Discord uses your data, sponsored content, and who sees your full profile. */
export function DataPrivacyPage() {
  const settings = useAccountSettings()
  const consent = useLoad('consent', () => host.discordAccount('consent'), data => data as { usageStatistics: boolean; personalization: boolean })
  // Which consent's Turn off question is open, and what Discord said when one didn't save.
  const [asking, setAsking] = useState<Consent | null>(null)
  const [consentError, setConsentError] = useState<string | null>(null)
  const setConsent = (kind: Consent, on: boolean) => host.discordAccount('setConsent', on ? { grant: [kind] } : { revoke: [kind] })
    .then(result => { consent.reload(); return result.ok ? null : result.message ?? "Discord didn't save that." }, () => "Discord didn't save that.")
  return (
    <>
      <h1>Data & Privacy</h1>
      <Category title="How Discord Uses My Data">
        <LoadError error={consent.error} onRetry={consent.reload} />
        <Row label="Use data to make Discord work" detail="Discord needs to store and process some data in order to provide you the basic Discord service, such as your messages, what servers you're in and your Direct Messages." />
        <ToggleRow label="Use data to improve Discord" detail="Allow Discord to use and process my information to understand and improve Discord's services."
          checked={consent.value?.usageStatistics ?? false} disabled={!consent.value}
          onChange={value => { if (value) return setConsent('usage_statistics', true); setAsking('usage_statistics') }} />
        <ToggleRow label="Use data to personalize my Discord experience" detail="Allows Discord to use information, such as who you talk to and what games you play, to personalize Discord for you."
          checked={consent.value?.personalization ?? false} disabled={!consent.value}
          onChange={value => { if (value) return setConsent('personalization', true); setAsking('personalization') }} />
        {asking ? (
          <div className="exo-dc-settings-ask exo-glass" role="alertdialog" aria-label={TURN_OFF[asking].title}>
            <span>{TURN_OFF[asking].body}</span>
            <button type="button" className="exo-set-btn" onClick={() => setAsking(null)}>No, take me back!</button>
            <button type="button" className="exo-set-btn is-danger" onClick={() => { setAsking(null); setConsentError(null); void setConsent(asking, false).then(setConsentError) }}>Yes, I&apos;m sure</button>
          </div>
        ) : null}
        {consentError ? <p className="exo-dc-settings-note" role="alert">{consentError}</p> : null}
        <AccountToggle label="Use my Discord activity to personalize Sponsored Content" detail="Allows Discord to personalize Sponsored Content, like Quests, using your Discord activity, such as the games you play."
          settings={settings} field="dropsOptedOut" invert />
        <AccountToggle label="Use third-party data to personalize Sponsored Content" detail="Allows Discord to personalize Sponsored Content, like Quests, using data Discord receives from advertisers and third-party data providers."
          settings={settings} field="quests3pDataOptedOut" invert disabled={settings.dropsOptedOut} />
        <LinkRow label="Request my data" detail="Get a copy of the data Discord keeps for your account, by email." button="Request Data" url="https://discord.com/settings/data-and-privacy" />
      </Category>
      <Category title="Profile Privacy" detail="Control who can see your profile info — like your bio and connected accounts.">
        <ChoiceRow label="Share my full profile with" value={settings.profileVisibility === 0 ? 3 : settings.profileVisibility}
          onChange={value => changeAccountSettings({ profileVisibility: value })}
          choices={[{ value: 3, label: 'Friends & All Servers' }, { value: 2, label: 'Friends & Small Servers Only' }, { value: 1, label: 'Friends Only' }]} />
        <AccountToggle label="Share when I update my profile" detail="Allow friends to receive a push notification when you update your profile."
          settings={settings} field="notifyFriendsOnProfileUpdate" />
      </Category>
    </>
  )
}

// What an unset default shows: the older setting (0 off, 1 large servers, 2 on) in the newer one's terms.
function activityDefault(settings: AccountSettings) {
  if (settings.defaultGuildsActivityRestrictedV2 !== 0) return settings.defaultGuildsActivityRestrictedV2
  return [1, 2, 3][settings.defaultGuildsActivityRestricted] ?? 1
}

/** Discord settings > Activity Privacy: what activity you share, where, and who can join your games. */
export function ActivityPage() {
  const settings = useAccountSettings()
  return (
    <>
      <h1>Activity Privacy</h1>
      <Category title="What Activity I Share">
        <AccountToggle label="Share my activity" detail="Display the game or app you're using as your status." settings={settings} field="showCurrentGame" />
        <AccountToggle label="Share when I come online" detail="Allow friends to receive a push notification when you come online." settings={settings} field="notifyFriendsOnComeOnline" />
        <AccountToggle label="Share when I start streaming" detail="Allow members that you interact with the most to get a notification when you start streaming in small servers."
          settings={settings} field="notifyServerMembersOnGoLive" />
      </Category>
      <Category title="Where I Share Activity">
        <ChoiceRow label="Automatically share my activity when joining a server" detail="Your activity is always shared with your friends."
          value={activityDefault(settings)} onChange={value => changeAccountSettings({ defaultGuildsActivityRestrictedV2: value })}
          choices={[{ value: 1, label: 'Share in all servers' }, { value: 2, label: 'Share in servers with 200 or fewer members' }, { value: 3, label: 'Do not share in any servers' }]} />
      </Category>
      <Category title="Who Can Join My Games" detail="Some games allow users to join the game you're playing when viewing your activity.">
        <AccountToggle label="Allow friends to join my game" detail="Allow friends to join my game without sending a request." settings={settings} field="allowActivityPartyPrivacyFriends" />
        <AccountToggle label="Allow voice channel participants to join my game"
          detail="Allow anyone in a shared voice channel to join my game without sending a request. This feature only works in non-Community enabled servers."
          settings={settings} field="allowActivityPartyPrivacyVoiceChannel" />
      </Category>
    </>
  )
}

/** Discord settings > Connected Apps: your connections, and the apps you've authorized (with Deauthorize). */
export function ConnectedAppsPage() {
  const hides = useStreamerHides()
  const connections = useLoad('connections', () => host.discordAccount('connections'), data => data as DiscordConnection[])
  const apps = useLoad('apps', () => host.discordAccount('apps'), data => data as DiscordAuthorizedApp[])
  const [failed, setFailed] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  // Discord asks before an app loses its link to the account.
  const [asking, setAsking] = useState<DiscordAuthorizedApp | null>(null)
  const deauthorize = (id: string) => {
    if (busy) return
    setAsking(null); setFailed(null); setBusy(id)
    const why = "Discord didn't deauthorize that app."
    void host.discordAccount('deauthorize', { id })
      .then(result => { if (result.ok) apps.reload(); else setFailed(result.message ?? why) }, () => setFailed(why))
      .finally(() => setBusy(null))
  }
  return (
    <>
      <h1>Connected Apps</h1>
      <Category title="Connections" detail="These are accounts from other platforms. Connecting them here can unlock new features on Discord.">
        <LoadError error={connections.error} onRetry={connections.reload} />
        {connections.value?.length === 0 ? <p className="exo-dc-settings-note">No accounts connected.</p> : null}
        {connections.value?.map((c, i) => (
          <Row key={`${c.type}-${i}`} label={connectionLabel(c.type)}
            detail={[hides.personal ? 'Hidden in Streamer Mode' : c.name, c.revoked ? 'Disconnected' : c.verified ? null : 'Not verified', c.visible ? 'On your profile' : null].filter(Boolean).join(' · ')} />
        ))}
        <LinkRow label="Add connections" detail="Connecting an account signs in to that service on Discord's site." button="Add" url="https://discord.com/settings/connections" />
      </Category>
      <Category title="Authorized Apps" detail="These include third-party services you've given permission to access your Discord account. You can review what each app can do and revoke access anytime.">
        <LoadError error={apps.error} onRetry={apps.reload} />
        {failed ? <p className="exo-dc-settings-note" role="alert">{failed}</p> : null}
        {apps.value?.length === 0 ? <p className="exo-dc-settings-note">You haven&apos;t authorized any apps.</p> : null}
        {apps.value?.map(app => (
          <Row key={app.id} label={app.name ?? 'App'} detail={app.scopes.join(', ')}>
            <button type="button" className="exo-set-btn is-danger" disabled={busy !== null} onClick={() => setAsking(app)}>Deauthorize</button>
          </Row>
        ))}
        {asking ? (
          <Confirm title="Deauthorize Application" action="Deauthorize" onClose={() => setAsking(null)} onConfirm={() => deauthorize(asking.id)}>
            <p>This action will remove the link between your Discord account and {asking.name ?? 'this app'}</p>
          </Confirm>
        ) : null}
      </Category>
    </>
  )
}
