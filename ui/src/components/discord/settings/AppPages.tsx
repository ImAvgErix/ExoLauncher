import { useEffect, useState } from 'react'
import { host, type LauncherSettings } from '../../../lib/host'
import { onPerformanceMode, performanceMode, setPerformanceMode } from '../../../lib/discord'
import { changeAccountSettings, setDiscordPrefs, useAccountSettings, useDiscordPrefs, type DiscordClientSettings } from '../../../lib/discordPrefs'
import { AccountToggle, Category, ChoiceRow, LoadError, Preview, Row, SliderRow, ToggleRow, useClientSettings } from './parts'

/** Discord settings > Notifications: Windows notifications, what Discord tells you about, its sounds and the badge. */
export function NotificationsPage() {
  const settings = useAccountSettings()
  const { settings: client, save, error, reload } = useClientSettings()
  const [launcher, setLauncher] = useState<LauncherSettings | null>(null)
  useEffect(() => { host.getSettings().then(setLauncher).catch(() => { }) }, [])
  const quiet = launcher?.discordQuietWhilePlaying !== false
  return (
    <>
      <h1>Notifications</h1>
      <LoadError error={error} onRetry={reload} />
      <Category title="Overview">
        <ToggleRow label="Enable Desktop Notifications" detail="Windows notifications for direct messages and mentions." checked={client?.desktopNotifications ?? true}
          disabled={!client} onChange={value => save({ desktopNotifications: value })} />
        <ToggleRow label="Enable Taskbar Flashing" detail="Flashes the app in your taskbar when you have new notifications." checked={client?.taskbarFlash ?? true}
          disabled={!client} onChange={value => save({ taskbarFlash: value })} />
        <ToggleRow label="Quiet while playing" detail="No notifications while a game runs. Unread messages wait in the badge." checked={quiet} disabled={!launcher}
          onChange={value => {
            setLauncher(s => s && { ...s, discordQuietWhilePlaying: value })
            return host.setSettings({ discordQuietWhilePlaying: value }).then(() => null, () => {
              setLauncher(s => s && { ...s, discordQuietWhilePlaying: !value })
              return "Exo didn't save that."
            })
          }} />
      </Category>
      <Category title="Notify me when...">
        <AccountToggle label="People I know start streaming in small servers" settings={settings} field="streamNotificationsEnabled" />
        <AccountToggle label="A friend and I reach a friendship anniversary" settings={settings} field="enableFriendAnniversaryNotifications" />
        <AccountToggle label="Friends come online" settings={settings} field="enableFriendOnlineNotifications" />
        <AccountToggle label="Friends update their profile" settings={settings} field="enableProfileUpdatesNotifications" />
        <ChoiceRow label="Someone reacts to my messages" value={settings.reactionNotifications} onChange={value => changeAccountSettings({ reactionNotifications: value })}
          choices={[{ value: 0, label: 'All Messages' }, { value: 1, label: 'Only Direct Messages' }, { value: 2, label: 'Never' }]} />
      </Category>
      <NotificationSounds client={client} save={save} />
      <Category title="Badges">
        <ToggleRow label="Enable Unread Message Badge" detail="Shows a red badge on the app icon when you have unread messages." checked={client?.unreadBadge ?? true}
          disabled={!client} onChange={value => save({ unreadBadge: value })} />
      </Category>
      <Category title="Advanced">
        <AccountToggle label="Allow playback and usage of /tts command"
          detail="Using the /tts command speaks your message out loud. Turn this off if you prefer to see /tts messages as text only."
          settings={settings} field="enableTtsCommand" />
      </Category>
    </>
  )
}

type Save = (change: Partial<DiscordClientSettings>) => Promise<string | null>

/** One of Discord's sounds as a switch with its Preview Sound. */
export function SoundRow({ label, sound, client, save }: { label: string; sound: string; client: DiscordClientSettings | null; save: Save }) {
  const off = client?.disabledSounds.includes(sound) ?? false
  return (
    <ToggleRow label={label} detail={<Preview sound={sound} />} checked={!off && !client?.disableAllSounds} disabled={!client || client.disableAllSounds}
      onChange={value => client ? save({ disabledSounds: value ? client.disabledSounds.filter(s => s !== sound) : [...client.disabledSounds, sound] }) : undefined} />
  )
}

function NotificationSounds({ client, save }: { client: DiscordClientSettings | null; save: Save }) {
  return (
    <Category title="Sounds">
      <SoundRow label="New Message" sound="message1" client={client} save={save} />
      <ToggleRow label="New Message in the channel I'm currently reading" detail={<Preview sound="message3" />}
        checked={(client?.selectedChannelSound ?? false) && !client?.disableAllSounds} disabled={!client || client.disableAllSounds}
        onChange={value => save({ selectedChannelSound: value })} />
      <SoundRow label="Incoming Ring" sound="call_ringing" client={client} save={save} />
      <SliderRow label="Sound Volume" detail="How loud all of Discord's sounds play, including voice & video sound effects."
        min={0} max={100} value={client?.soundVolume ?? 60} render={v => `${v}%`} disabled={!client || client.disableAllSounds}
        onCommit={v => void save({ soundVolume: v })} />
      <ToggleRow label="Disable All Notification Sounds"
        detail="Disables all notification sounds, including voice & video sound effects. Your individual sound preferences will be saved and restored if you turn this on."
        checked={client?.disableAllSounds ?? false} disabled={!client} onChange={value => save({ disableAllSounds: value })} />
    </Category>
  )
}

/** Discord settings > Appearance: how messages show, the chat box, and Streamer Mode. */
export function AppearancePage() {
  const settings = useAccountSettings()
  const prefs = useDiscordPrefs()
  return (
    <>
      <h1>Appearance</h1>
      <Category title="Messages">
        <h3 className="exo-dc-settings-fieldset">Show images, videos, and lolcats...</h3>
        <AccountToggle label="When posted as links to chat" settings={settings} field="inlineEmbedMedia" />
        <AccountToggle label="When uploaded directly to Discord" settings={settings} field="inlineAttachmentMedia" />
        <AccountToggle label="Show embeds and link previews" settings={settings} field="renderEmbeds" />
        <AccountToggle label="Show emoji reactions" settings={settings} field="renderReactions" />
        <ChoiceRow label="Show spoiler content" value={settings.renderSpoilers} onChange={value => changeAccountSettings({ renderSpoilers: value })}
          choices={[{ value: 'ON_CLICK', label: 'On click' }, { value: 'ALWAYS', label: 'Always' }, { value: 'IF_MODERATOR', label: 'On servers I moderate' }]} />
        <ToggleRow label="Show user avatars" detail={settings.messageDisplayCompact ? undefined : 'To hide avatars, go to Accessibility and select Compact under Chat Message Display.'}
          checked={!settings.messageDisplayCompact || settings.displayCompactAvatars} disabled={!settings.messageDisplayCompact}
          onChange={value => changeAccountSettings({ displayCompactAvatars: value })} />
      </Category>
      <Category title="Chat Box">
        <AccountToggle label="Automatically convert emoticons in your messages to emoji" detail="For example, when you type :) Discord will convert it to :slight_smile:"
          settings={settings} field="convertEmoticons" />
        <AccountToggle label="Show stickers in autocomplete results" settings={settings} field="includeStickersInAutocomplete" />
      </Category>
      <Category title="Streamer Mode">
        <ToggleRow label="Enable Streamer Mode" detail="Streamer Mode hides your personal info so others watching your stream can't see it."
          checked={prefs.streamerMode} onChange={value => setDiscordPrefs({ streamerMode: value })} />
        <h3 className="exo-dc-settings-fieldset">If Streamer Mode is enabled...</h3>
        <ToggleRow label="Hide my personal information, such as email, connected accounts, and notes" checked={prefs.streamerHidePersonal}
          onChange={value => setDiscordPrefs({ streamerHidePersonal: value })} />
        <ToggleRow label="Hide invite links to my Discord servers" checked={prefs.streamerHideInvites} onChange={value => setDiscordPrefs({ streamerHideInvites: value })} />
        <ToggleRow label="Disable all sound effects" checked={prefs.streamerDisableSounds} onChange={value => setDiscordPrefs({ streamerDisableSounds: value })} />
        <ToggleRow label="Disable notifications" checked={prefs.streamerDisableNotifications} onChange={value => setDiscordPrefs({ streamerDisableNotifications: value })} />
      </Category>
    </>
  )
}

const FONT_SIZES = [12, 14, 15, 16, 18, 20, 24]
const GROUP_SPACINGS = [0, 4, 8, 16, 24]
const ZOOMS = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200]

/** A slider over Discord's own stops (font sizes, spacings, zoom levels): it moves stop to stop. */
function StopsRow({ label, detail, stops, value, unit, onCommit }: { label: string; detail: string; stops: number[]; value: number; unit: string; onCommit: (value: number) => void }) {
  const at = Math.max(0, stops.indexOf(value))
  return <SliderRow label={label} detail={detail} min={0} max={stops.length - 1} value={at} render={i => `${stops[i]}${unit}`} onCommit={i => onCommit(stops[i])} />
}

/** Discord settings > Accessibility: text size, density, zoom, color, motion and text-to-speech. */
export function AccessibilityPage() {
  const settings = useAccountSettings()
  const prefs = useDiscordPrefs()
  const [reduced, setReduced] = useState(performanceMode)
  useEffect(() => onPerformanceMode(() => setReduced(performanceMode())), [])
  return (
    <>
      <h1>Accessibility</h1>
      <Category title="Text Readability">
        <StopsRow label="Text size in chat" detail="Adjust the size of the chat font." stops={FONT_SIZES} value={prefs.fontSize} unit="px" onCommit={v => setDiscordPrefs({ fontSize: v })} />
        <ToggleRow label="Always underline links" detail="Make links stand out more." checked={prefs.underlineLinks} onChange={value => setDiscordPrefs({ underlineLinks: value })} />
      </Category>
      <Category title="Visual Density">
        <ChoiceRow label="Chat Message Display" detail="Change the appearance of chat messages." value={settings.messageDisplayCompact ? 'compact' : 'default'}
          onChange={value => changeAccountSettings({ messageDisplayCompact: value === 'compact' })}
          choices={[{ value: 'default', label: 'Default' }, { value: 'compact', label: 'Compact' }]} />
        <StopsRow label="Space Between Message Groups" detail="Adjust the spacing between message groups." stops={GROUP_SPACINGS}
          value={prefs.groupSpacing ?? (settings.messageDisplayCompact ? 0 : 16)} unit="px"
          onCommit={v => setDiscordPrefs({ groupSpacing: v })} />
        <StopsRow label="Zoom level" detail="Adjust the size of Discord in Exo." stops={ZOOMS} value={prefs.zoom} unit="%" onCommit={v => setDiscordPrefs({ zoom: v })} />
      </Category>
      <Category title="Color & Contrast">
        <SliderRow label="Saturation" detail="Reduce the saturation of colors within the app, for those with color sensitivities. This does not affect images, videos, role colors or other user content."
          min={0} max={1} step={0.05} value={prefs.saturation} render={v => `${Math.round(v * 100)}%`} onCommit={v => setDiscordPrefs({ saturation: v })} />
        <ToggleRow label="Apply saturation setting to custom colors" detail="Apply the above setting to custom color choices, like role colors."
          checked={prefs.desaturateUserColors} onChange={value => setDiscordPrefs({ desaturateUserColors: value })} />
        <ChoiceRow label="Role Colors" detail="Choose how you would like role colors to be displayed." value={prefs.roleStyle} onChange={value => setDiscordPrefs({ roleStyle: value })}
          choices={[{ value: 'username', label: 'In names' }, { value: 'dot', label: 'Next to names' }, { value: 'hidden', label: "Don't show role colors" }]} />
      </Category>
      <Category title="Reduced Motion">
        <ToggleRow label="Enable Reduced Motion" detail="Reduce the amount and intensity of animations, hover effects, and other moving effects. GIFs hold still until you point at them."
          checked={reduced} onChange={setPerformanceMode} />
        <AccountToggle label="Play GIFs when Discord is focused" settings={settings} field="gifAutoPlay" disabled={reduced} />
        <AccountToggle label="Play animated emoji" settings={settings} field="animateEmoji" disabled={reduced} />
        <ChoiceRow label="Play sticker animations" value={settings.animateStickers} onChange={value => changeAccountSettings({ animateStickers: value })}
          choices={[{ value: 0, label: 'Always animate' }, { value: 1, label: 'Animate on interaction' }, { value: 2, label: 'Never animate' }]} />
      </Category>
      <Category title="Audio & Screen Reader">
        <SliderRow label="Text-to-Speech rate" detail="Control the speed at which /tts messages are read aloud." min={0.5} max={2} step={0.25} value={prefs.ttsRate}
          render={v => `x${v.toFixed(2)}`} onCommit={v => setDiscordPrefs({ ttsRate: v })} />
        <AccountToggle label="Show image descriptions" detail="Image descriptions are used to describe images for screen readers." settings={settings} field="viewImageDescriptions" />
      </Category>
    </>
  )
}

const LANGUAGES: { value: string; label: string }[] = [
  ['en-US', 'English, US'], ['en-GB', 'English, UK'], ['bg', 'български'], ['zh-CN', '中文'], ['zh-TW', '繁體中文'], ['hr', 'Hrvatski'], ['cs', 'Čeština'],
  ['da', 'Dansk'], ['nl', 'Nederlands'], ['fi', 'Suomi'], ['fr', 'Français'], ['de', 'Deutsch'], ['el', 'Ελληνικά'], ['hi', 'हिंदी'], ['hu', 'Magyar'],
  ['it', 'Italiano'], ['ja', '日本語'], ['ko', '한국어'], ['lt', 'Lietuviškai'], ['no', 'Norsk'], ['pl', 'Polski'], ['pt-BR', 'Português do Brasil'],
  ['ro', 'Română'], ['ru', 'Русский'], ['es-ES', 'Español'], ['es-419', 'Español, LATAM'], ['sv-SE', 'Svenska'], ['th', 'ไทย'], ['tr', 'Türkçe'],
  ['uk', 'Українська'], ['vi', 'Tiếng Việt'],
].map(([value, label]) => ({ value, label }))

/** Discord settings > Language & Time: the account's language and the time format in chat. */
export function LanguagePage() {
  const settings = useAccountSettings()
  const locale = settings.locale || (LANGUAGES.some(l => l.value === navigator.language) ? navigator.language : 'en-US')
  return (
    <>
      <h1>Language & Time</h1>
      <Category>
        <ChoiceRow label="Language" detail="The language Discord uses for your account: its emails, and notifications on your phone." value={locale} choices={LANGUAGES}
          onChange={value => changeAccountSettings({ locale: value })} />
        <ChoiceRow label="Time format" value={settings.timestampHourCycle} onChange={value => changeAccountSettings({ timestampHourCycle: value })}
          choices={[{ value: 0, label: 'Auto' }, { value: 1, label: '12-hour' }, { value: 2, label: '24-hour' }]} />
      </Category>
    </>
  )
}

/** Discord settings > Developer: Developer Mode, which adds Copy ID to menus. */
export function DeveloperPage() {
  const settings = useAccountSettings()
  return (
    <>
      <h1>Developer</h1>
      <Category>
        <AccountToggle label="Developer Mode" detail="Developer Mode exposes context menu items helpful for people writing apps using the Discord API." settings={settings} field="developerMode" />
      </Category>
    </>
  )
}

/** Log Out, as Discord asks it. */
export function LogOutPage({ onDone }: { onDone: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const failed = () => { setBusy(false); setError("Exo couldn't log you out. Try again.") }
  return (
    <>
      <h1>Log Out</h1>
      <Row label="Are you sure you want to logout?" detail="Exo forgets your Discord sign-in on this PC." error={error}>
        <button type="button" className="exo-set-btn is-danger" disabled={busy}
          onClick={() => { setBusy(true); setError(null); void host.discordSignOut().then(result => { if (result.ok) onDone(); else failed() }, failed) }}>Log Out</button>
      </Row>
    </>
  )
}
