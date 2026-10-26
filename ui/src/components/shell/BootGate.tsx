import { Activity, Suspense, lazy, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { Button } from '../ui/Button'
import { host, type LauncherSettings, type StoreStatus } from '../../lib/host'
import { ErrorBoundary } from './ErrorBoundary'
import { shouldReduceMotion } from '../../lib/motionPreference'
import type { View } from './TitleBar'
import { useChildRef, useDismiss } from '../../lib/dismiss'

let painted = false

/** A beat after the page's first two frames before the intro plays. */
const INTRO_SETTLE_MS = 120

const OnboardingPanel = lazy(() => import('../OnboardingPanel').then((m) => ({ default: m.OnboardingPanel })))

export function BootSplash({ revealing = false, onComplete, reduceMotion = shouldReduceMotion(), checkingUpdate = false }: {
  revealing?: boolean
  onComplete?: () => void
  reduceMotion?: boolean
  checkingUpdate?: boolean
}) {
  const root = useRef<HTMLDivElement>(null)
  const finish = useRef(onComplete)
  finish.current = onComplete
  const [playing, setPlaying] = useState(false)
  // The splash is the page's first frame: once it is drawn, the host shows the page.
  useEffect(() => {
    if (painted) return
    painted = true
    requestAnimationFrame(() => requestAnimationFrame(() => { void host.shellPainted().catch(() => { }) }))
  }, [])
  // The page's first frames are its busiest: the intro starts once two frames have drawn and a beat has passed.
  useEffect(() => {
    let timer = 0
    const frame = requestAnimationFrame(() => requestAnimationFrame(() => { timer = window.setTimeout(() => setPlaying(true), INTRO_SETTLE_MS) }))
    return () => { cancelAnimationFrame(frame); window.clearTimeout(timer) }
  }, [])
  useEffect(() => {
    if (!revealing || !root.current) return
    const surface = root.current
    const mark = surface.querySelector<HTMLElement>('.exo-boot-icon')
    if (!mark || typeof mark.animate !== 'function' || reduceMotion) {
      finish.current?.()
      return
    }
    // Exo, already drawn underneath, comes up as the mark sinks back and fades: transform and
    // opacity only, so it stays on the compositor and sharp (it shrinks, never grows past its raster).
    const recede = mark.animate([
      { transform: 'translateY(0) scale(1)', opacity: 1 },
      { transform: 'translateY(-6px) scale(0.86)', opacity: 0 },
    ], { duration: 380, easing: 'cubic-bezier(0.7, 0, 0.84, 0)', fill: 'forwards' })
    const fade = surface.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 520, delay: 120, easing: 'cubic-bezier(0.33, 1, 0.68, 1)', fill: 'forwards' })
    fade.onfinish = () => finish.current?.()
    return () => { fade.cancel(); recede.cancel() }
  }, [revealing, reduceMotion])
  // The mark is the launch screen's one wait (DESIGN.md › Waits): nothing here has a measure to show.
  return <div ref={root} className={`exo-boot${playing ? ' is-playing' : ''}`} role="status" aria-label={checkingUpdate ? 'Checking for updates' : 'Preparing library and game tools'}>
    <img className="exo-boot-icon" src="/logo.svg" width={128} height={128} alt="" draggable={false} decoding="sync" />
  </div>
}

/**
 * The boot splash's frame, as a plain element (never its own component), so React keeps the one
 * BootSplash mounted from the update check through settings to the library: its intro plays once.
 */
function splash(children?: ReactNode) {
  return (
    <div className="exo-app">
      {children ?? <BootSplash />}
    </div>
  )
}

export type BootGateProps = {
  settings: LauncherSettings | null
  settingsError: string | null
  storeMatrixReady: boolean
  stores: StoreStatus[]
  onRetrySettings: () => void
  onSettings: (next: LauncherSettings) => void
  onStores: (next: StoreStatus[]) => void
  onComplete: (refreshLibrary?: boolean) => Promise<void>
  onAddFolder: () => Promise<boolean>
}

/**
 * What stands in front of the shell before it may render: a settings load or
 * first-run onboarding. Null means "show the shell".
 */
export function bootGate(props: BootGateProps): ReactNode {
  const { settings, settingsError, storeMatrixReady } = props
  // Wait for settings so we don't flash library before first-run connect.
  if (!settings || (!settings.onboardingComplete && !storeMatrixReady)) {
    if (!settingsError) return splash()
    return (
      splash(
        <div className="flex flex-1 items-center justify-center px-6">
          <div className="exo-glass is-panel max-w-md rounded-[18px] p-6 text-center" role="alert">
            <h1 className="text-[length:calc(20px*var(--exo-scale,1))] font-semibold text-fg">Settings could not be loaded</h1>
            <p className="mt-2 text-[length:calc(12px*var(--exo-scale,1))] leading-relaxed text-muted">{settingsError}</p>
            <Button type="button" size="sm" className="exo-set-btn is-primary mt-5" onPress={props.onRetrySettings}>
              Try again
            </Button>
          </div>
        </div>
      )
    )
  }
  if (!settings.onboardingComplete) return <Onboarding {...props} />
  return null
}

function Onboarding({ stores, onSettings, onStores, onComplete, onAddFolder }: BootGateProps) {
  return (
    <Suspense fallback={splash()}>
      <OnboardingPanel
        stores={stores}
        onSettings={onSettings}
        onStores={onStores}
        onComplete={onComplete}
        onAddFolder={onAddFolder}
      />
    </Suspense>
  )
}

/** Rooms LauncherApp gives back while hidden (its sweep); only these keep scroll. */
export const EJECTABLE_ROOMS: readonly View[] = ['store', 'system', 'settings']

/** Where each of those rooms' scroller sat when it was last scrolled, so an
 *  unmounted room comes back where it was. Scroll events fire only on the
 *  scroller itself, so the room listens on it once found. */
const roomScroll = new Map<string, number>()

/** The moves a view arrives with (DESIGN.md › Motion), as opposed to loops and work. */
const ENTRANCES = new Set(['exo-arrive', 'exo-switch', 'exo-enter', 'exo-enter-cover', 'exo-fade-in', 'exo-float-in'])

/**
 * A retained room: mounted once opened, then React 19.2 Activity hides it
 * so off-screen rooms are not five hot trees. A crash stays inside the room.
 * Activity runs no effects while hidden, so a room preloaded behind the splash
 * is warm instead: live (its effects read its data) but not shown, until the
 * splash is gone and its reads have landed. A warm room is laid out at its real size out of
 * sight (exo-shell.css › is-backstage), never display:none: lists that size themselves render
 * their rows, words are shaped and the code that draws them has run, so its first showing costs
 * what a later one does (Discord's first frame was 109 ms, later ones 30).
 * A room arrives once: hidden is display:none, which restarts every CSS animation in it,
 * so when it comes back its entrances finish at once and it is simply there, as it was left.
 * A floating room (Settings) keeps its own open motion each time.
 */
export function Room({ name, mounted, active, covered = false, warm = false, label, className, onBackdrop, children }: {
  /** Which room, for what looks for it on the page (a room opened the first time waits on its pictures). */
  name?: string
  mounted: boolean
  active: boolean
  /** Under a floating room (Settings): still on screen and laid out as it was, but out of reach. */
  covered?: boolean
  warm?: boolean
  label: string
  className: string
  /** A floating room (Settings): a press outside its panel. */
  onBackdrop?: () => void
  children: ReactNode
}) {
  const frame = useRef<HTMLDivElement>(null)
  const arrived = useRef(false)
  const floating = !!onBackdrop
  // The panel is the room's one child (Settings).
  useDismiss(useChildRef(frame), onBackdrop, { modal: true, on: floating && active })
  useLayoutEffect(() => {
    const room = frame.current
    if (!active || !room) return
    if (arrived.current) {
      for (const animation of room.getAnimations({ subtree: true })) {
        if (!(animation instanceof CSSAnimation) || !ENTRANCES.has(animation.animationName)) continue
        const target = animation.effect instanceof KeyframeEffect ? animation.effect.target : null
        if (floating && (target === room || target?.parentElement === room)) continue
        animation.finish()
      }
    }
    arrived.current = true
  }, [active, floating])
  // The scroller appears with the room's children (a first-open chunk can suspend),
  // so it is found on the next frames; then it is listened to and, for a remount,
  // returned to where it was once its content is tall enough again.
  useEffect(() => {
    const room = frame.current
    if (!mounted || !name || !room) return
    let scroller: HTMLElement | null = null
    const find = () => room.querySelector<HTMLElement>('main') ??
      [...room.querySelectorAll<HTMLElement>('*')]
                .find(el => el.scrollHeight > el.clientHeight + 8 && /auto|scroll/.test(getComputedStyle(el).overflowY)) ?? null
    let tries = 90
    const attach = () => {
      scroller ??= find()
      if (!scroller) { if (--tries > 0) requestAnimationFrame(attach); return }
      const saved = roomScroll.get(name)
      const save = () => { if (scroller) roomScroll.set(name, scroller.scrollTop) }
      scroller.addEventListener('scroll', save, { passive: true })
      if (saved == null) return
      let retop = 30
      const restore = () => {
        if (!scroller) return
        scroller.scrollTop = saved
        if (Math.abs(scroller.scrollTop - saved) > 1 && --retop > 0) requestAnimationFrame(restore)
      }
      requestAnimationFrame(restore)
    }
    requestAnimationFrame(attach)
    return () => { tries = 0 }
  }, [mounted, name])
  if (!mounted) return null
  const backstage = warm && !active && !floating
  return (
    <Activity mode={active || warm ? 'visible' : 'hidden'}>
      <div ref={frame} data-room={name} className={`${className} exo-room${backstage ? ' is-backstage' : ''}`}
        hidden={(!active && !backstage) || undefined} inert={backstage || (active && covered) || undefined}>
        <ErrorBoundary label={label}>
          <Suspense fallback={null}>{children}</Suspense>
        </ErrorBoundary>
      </div>
    </Activity>
  )
}
