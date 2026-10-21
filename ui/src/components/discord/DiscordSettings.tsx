import { useEffect, useRef } from 'react'
import { Close } from '../../brand/icons'
import type { DiscordHome } from '../../lib/discord'
import { useDialogFocus } from '../../lib/focusTrap'
import { loadAccountSettings } from '../../lib/discordPrefs'
import { useGlide } from '../../lib/glide'
import { AccountPage, DevicesPage, ProfilesPage } from './settings/AccountPages'
import { AccessibilityPage, AppearancePage, DeveloperPage, LanguagePage, LogOutPage, NotificationsPage } from './settings/AppPages'
import { SystemPage } from './settings/KeybindsPage'
import { ActivityPage, ConnectedAppsPage, DataPrivacyPage, MessagingPage } from './settings/PrivacyPages'
import { SettingsLayer } from './SettingsLayer'
import { VoicePage } from './settings/VoicePage'
import { pushPage } from '../../lib/viewTransition'

export type DiscordSettingsSection =
  | 'profiles' | 'account' | 'devices' | 'privacy' | 'messaging' | 'notifications'
  | 'voice' | 'appearance' | 'accessibility' | 'system' | 'language'
  | 'activity' | 'apps' | 'developer' | 'logout'

// Discord's User Settings sidebar (October 2026), with what applies to Exo's client: Billing, Clips,
// Family Center, Registered Games and the Game Overlay are Discord app features Exo's client doesn't have.
const GROUPS: { title?: string; items: { id: DiscordSettingsSection; label: string }[] }[] = [
  { items: [{ id: 'profiles', label: 'Profiles' }] },
  {
    items: [
      { id: 'account', label: 'Account' },
      { id: 'privacy', label: 'Data & Privacy' },
      { id: 'messaging', label: 'Messaging Permissions' },
      { id: 'notifications', label: 'Notifications' },
    ],
  },
  {
    title: 'Experience',
    items: [
      { id: 'voice', label: 'Voice & Video' },
      { id: 'appearance', label: 'Appearance' },
      { id: 'accessibility', label: 'Accessibility' },
      { id: 'system', label: 'System' },
      { id: 'language', label: 'Language & Time' },
    ],
  },
  {
    title: 'Games & Apps',
    items: [
      { id: 'activity', label: 'Activity Privacy' },
      { id: 'apps', label: 'Connected Apps' },
    ],
  },
  {
    items: [
      { id: 'developer', label: 'Developer' },
      { id: 'logout', label: 'Log Out' },
    ],
  },
]

/**
 * Discord's own User Settings for Exo's Discord client, laid out as Discord lays them out: the
 * sidebar's sections and the section beside it. The account's settings are Discord's (every Discord
 * app shares them); what Discord keeps per device is kept on this PC. Exo's own settings stay in Exo's.
 */
export function DiscordSettings({ section, home, onSection, onClose }: {
  section: DiscordSettingsSection
  home: DiscordHome | null
  onSection: (section: DiscordSettingsSection) => void
  onClose: () => void
}) {
  const glide = useGlide<HTMLDivElement>()
  const dialog = useRef<HTMLDivElement>(null)
  useDialogFocus(dialog)
  // Escape closes Settings once it reaches the window: an open menu, a keybind being recorded or a dialog over
  // Settings takes it first.
  useEffect(() => {
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented || document.querySelector('.exo-dc-modal-back')) return
      event.preventDefault()
      onClose()
    }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [onClose])
  useEffect(loadAccountSettings, [])
  const current = section === 'devices' ? 'account' : section
  return (
    <SettingsLayer onClose={onClose}>
    <div ref={dialog} className="exo-dc-settings" role="dialog" aria-modal="true" aria-label="Discord settings" tabIndex={-1}>
      <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
      <nav className="exo-dc-settings-nav" aria-label="Discord settings sections">
        <div ref={glide} className="exo-glide">
          {GROUPS.map((group, i) => (
            <div key={i} className="exo-dc-settings-group">
              {group.title ? <h2>{group.title}</h2> : null}
              <ul>
                {group.items.map(item => (
                  <li key={item.id}>
                    <button type="button" aria-current={current === item.id ? 'true' : undefined}
                      className={[current === item.id ? 'is-current' : '', item.id === 'logout' ? 'is-danger' : ''].filter(Boolean).join(' ') || undefined}
                      onClick={() => void pushPage(() => onSection(item.id), '.exo-dc-settings-page', '.exo-dc-settings-nav [aria-current="true"]')}>{item.label}</button>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      </nav>
      <div className="exo-dc-settings-body">
        <button type="button" className="exo-game-close exo-dc-settings-close" aria-label="Close Discord settings" onClick={onClose}><Close size={16} /></button>
        <div key={section} className="exo-dc-settings-page exo-motion-switch">
          <Page section={section} me={home?.me ?? null} guilds={home?.guilds ?? []} onSection={onSection} onClose={onClose} />
        </div>
      </div>
    </div>
    </SettingsLayer>
  )
}

function Page({ section, me, guilds, onSection, onClose }: {
  section: DiscordSettingsSection
  me: DiscordHome['me']
  guilds: DiscordHome['guilds']
  onSection: (section: DiscordSettingsSection) => void
  onClose: () => void
}) {
  switch (section) {
    case 'profiles': return <ProfilesPage home={me} guilds={guilds} />
    case 'account': return <AccountPage home={me} onProfiles={() => onSection('profiles')} onDevices={() => onSection('devices')} />
    case 'devices': return <DevicesPage onBack={() => onSection('account')} />
    case 'privacy': return <DataPrivacyPage />
    case 'messaging': return <MessagingPage guilds={guilds} />
    case 'notifications': return <NotificationsPage />
    case 'voice': return <VoicePage />
    case 'appearance': return <AppearancePage />
    case 'accessibility': return <AccessibilityPage />
    case 'system': return <SystemPage />
    case 'language': return <LanguagePage />
    case 'activity': return <ActivityPage />
    case 'apps': return <ConnectedAppsPage />
    case 'developer': return <DeveloperPage />
    case 'logout': return <LogOutPage onDone={onClose} />
  }
}
