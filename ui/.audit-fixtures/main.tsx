// Local audit harness only; Vite's production input excludes this directory.
import React from 'react'
import { createRoot } from 'react-dom/client'
import '../src/tokens.css'
import '../src/exo-shell.css'
import '../src/styles/rooms.css'
import '../src/styles/hosted-room.css'
import { host } from '../src/lib/host'
import { applyTextTheme } from '../src/lib/exoTheme'
import App from '../src/App'
import { OnboardingPanel } from '../src/components/OnboardingPanel'
import { DiscordClientRoom } from '../src/components/DiscordClientRoom'
import { MusicRoom } from '../src/components/MusicRoom'
import { ActivateKeySheet } from '../src/components/ActivateKeySheet'

const query = new URLSearchParams(location.search), scenario = query.get('scenario') || 'app'
const noop = () => { }, settings = await host.getSettings(), library = await host.getLibrary()
const account = { ok: true, signedIn: scenario === 'account-in', configured: true, providers: ['password', 'google', 'email'], roles: [], canManageBadges: false, badges: [], email: 'a-very-long-account-name-for-fitting-checks@example.test' }
host.accountGet = async () => account
host.onlineHealth = async () => ({ ok: true, value: { capabilities: { providers: { password: true, google: true, email: true } } }, diagnostics: {} })
host.onlineExportAccount = host.onlineDeleteAccount = async () => ({ ok: false, diagnostics: { error: { message: 'Fixture: request failed. Your data is unchanged.' } } })
host.accountSignIn = host.accountSignInPassword = host.accountCreatePassword = async () => ({ ok: false, code: 'INVALID_CREDENTIALS', message: 'Fixture: sign-in rejected.' })
host.getSettings = async () => ({ ...settings, accountSetupComplete: true, reduceMotion: query.has('reduce'), textSize: query.has('large') ? 'large' : 'normal' })
document.documentElement.dataset.exoMotion = query.has('reduce') ? 'reduce' : 'full'
applyTextTheme({ textColour: 'white', textSize: query.has('large') ? 'large' : 'normal' })
if (scenario === 'library-empty') host.getLibrary = async () => ({ ...library, games: [], favorites: [] })
if (scenario === 'library-error') host.getLibrary = async () => { throw Error('Fixture: library scan interrupted. Retry to recover.') }
if (scenario === 'library-large') host.getLibrary = async () => ({ ...library, games: Array.from({ length: 5000 }, (_, i) => ({ ...library.games[2], id: `fixture:${i}`, title: `${i.toString().padStart(4, '0')} A deliberately long title that should truncate without pushing controls out of the window`, isFavorite: false })), favorites: [] })
if (scenario.startsWith('game-')) {
  const game = { ...library.games.find(g => g.id === 'mock:hades')!, title: 'A deliberately long game title with an extended edition subtitle for fitting checks', isFavorite: false, isRunning: scenario === 'game-running', canStop: scenario === 'game-running' }
  const progress = { gameId: game.id, phase: 'downloading', percent: 63.4, bytesPerSecond: 12500000, bytesDownloaded: 630000000, bytesToDownload: 1000000000, status: 'Downloading', canCancel: true, isActive: scenario === 'game-transfer' }
  host.getLibrary = async () => ({ ...library, games: [game], favorites: [], progress })
  host.getGame = async () => ({ ok: true, game })
  host.progress = async () => progress
  host.dlssStatus = async () => scenario === 'game-loading' ? await new Promise(() => { }) : scenario === 'game-failed' ? { ok: false, message: 'Fixture: versions could not be checked.', items: [] } : { ok: true, items: scenario === 'game-empty' ? [] : ['nvngx_dlss.dll', 'nvngx_dlssg.dll', 'nvngx_dlssd.dll', 'amd_fidelityfx_dx12.dll', 'libxess.dll', 'libxess_dx11.dll', 'libxess_fg.dll', 'libxell.dll'].map(fileName => ({ fileName, present: true, eligible: true, currentVersion: '1.2.3.4', packVersion: '2.3.4.5', canRestore: true })) }
}
if (scenario === 'account-error') host.accountGet = async () => { throw Error('Fixture: account connection unavailable.') }
if (scenario === 'account-loading') host.accountGet = () => new Promise(() => { })
if (scenario === 'discord-error') host.friendsStatus = async () => ({ ok: false, state: 'closed', message: 'Fixture: Discord could not load.' })
if (scenario === 'music-error') host.musicSetBounds = async () => ({ ok: false, service: 'spotify' as const, state: 'error', message: 'Fixture: Spotify could not load.' })
if (scenario === 'mini-player') {
  localStorage.setItem('exo.musicWebService', 'youtube')
  const webPreferences = await host.webClientPreferences()
  host.webClientPreferences = async () => ({ ...webPreferences, musicService: 'youtube' as const })
  host.musicPlayback = async () => ({ ok: true, playback: { provider: 'youtube', status: 'playing', title: 'An unusually long song title for checking the caption controls', artist: 'A deliberately long artist name', artworkDataUrl: null, positionMs: 12_000, durationMs: 203_000, positionAgeMs: 0, rate: 1, canPlay: true, canPause: true, canPrev: true, canNext: true, canSeek: true } })
  host.musicVolume = async () => ({ ok: true, level: 0.36 })
}
const box = (child: React.ReactNode) => <div className="exo-app"><main style={{ padding: 32, overflow: 'auto', height: '100vh', display: 'flex', flexDirection: 'column' }}>{child}</main></div>
let content: React.ReactNode = <App />
if (scenario === 'onboarding') content = <OnboardingPanel stores={await host.storesMatrix()} message={null} onSettings={noop} onStores={noop} onComplete={async () => { }} onAddFolder={async () => { }} />
if (scenario === 'discord-error') content = box(<DiscordClientRoom active />)
if (scenario === 'music-error') content = box(<MusicRoom service="spotify" active />)
if (scenario === 'activate') content = <div className="exo-app"><ActivateKeySheet open onClose={noop} onReady={noop} onStatus={noop} /></div>
createRoot(document.getElementById('root')!).render(content)
