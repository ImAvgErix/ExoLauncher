import type { ReactNode, RefObject } from 'react'
import { Button } from './ui/Button'

export type HostedState = 'loading' | 'installing' | 'notInstalled' | 'running' | 'closed' | 'error'

/**
 * The measured area the host draws a provider's own window over. Exo only
 * speaks while the window is not there: installing, opening, closed, failed.
 */
export function HostedRoom({ name, surface, label, state, message, installLabel, retryLabel = 'Open again', onInstall, onRetry, extra }: {
  name: string
  surface: RefObject<HTMLDivElement | null>
  label?: string
  state: HostedState
  message?: string | null
  installLabel: string
  retryLabel?: string
  onInstall: () => void
  onRetry: () => void
  /** A second action beside retry, e.g. opening the web player in a browser. */
  extra?: ReactNode
}) {
  return (
    <main className="exo-hosted-room">
      <div className="exo-hosted-room-surface" ref={surface} aria-label={label}>
        {state === 'notInstalled' ? (
          <div className="exo-hosted-room-state" role="status">
            <p>{name} isn't installed</p>
            <Button size="sm" className="exo-set-btn" onPress={onInstall}>{installLabel}</Button>
          </div>
        ) : state === 'installing' ? (
          <p className="exo-hosted-room-state" role="status">{message ?? `Installing ${name}…`}</p>
        ) : state === 'closed' || state === 'error' ? (
          <div className="exo-hosted-room-state" role="alert">
            <p>{message ?? (state === 'closed' ? `${name} closed.` : `${name} could not load.`)}</p>
            <Button size="sm" className="exo-set-btn" onPress={onRetry}>{retryLabel}</Button>
            {extra}
          </div>
        ) : state === 'loading' ? (
          <p className="exo-hosted-room-state" role="status">Opening {name}…</p>
        ) : null}
      </div>
    </main>
  )
}
