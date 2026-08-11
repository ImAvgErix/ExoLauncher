import { Loader2 } from 'lucide-react'
import type { StoreStatus } from '../lib/host'
import { cn } from '../lib/utils'

export function OnboardingPanel({
  stores,
  authBusy,
  authMsg,
  onAuth,
  onContinue,
  onSkip,
}: {
  stores: StoreStatus[]
  authBusy: string | null
  authMsg: string | null
  onAuth: (store: string) => void
  onContinue: () => void
  onSkip: () => void
}) {
  const rows = (stores.length
    ? stores
    : [
        { store: 'steam', displayName: 'Steam', agentPresent: false },
        { store: 'epic', displayName: 'Epic', agentPresent: false },
        { store: 'gog', displayName: 'GOG', agentPresent: false },
        { store: 'riot', displayName: 'Riot', agentPresent: false },
      ]
  ).filter((s) => s.store !== 'local')

  return (
    <div className="exo-app">
      <div className="relative z-10 flex min-h-0 flex-1 flex-col items-center justify-center px-10 py-12">
        <div className="w-full max-w-lg">
          <div className="mb-8 flex items-center gap-3">
            <img src="./logo.png" alt="" className="h-9 w-9" width={36} height={36} draggable={false} />
            <span className="text-lg font-semibold tracking-tight">Exo</span>
          </div>

          <h1 className="text-[28px] font-semibold tracking-tight text-fg">Connect</h1>

          {authMsg && (
            <div className="mt-5 rounded-xl border border-line-soft bg-elevated px-4 py-3 text-[13px] text-fg">
              {authMsg}
            </div>
          )}

          <ul className="mt-8 space-y-2">
            {rows.map((s) => {
              const clientInstalled = s.clientPresent ?? s.agentPresent
              const backendAvailable = !!s.agentPresent
              // A bundled/headless backend is not proof that the vendor's
              // visible client is installed.
              const accountConnected = backendAvailable && !!s.signedIn
              const connected = clientInstalled && accountConnected
              const needsAuth = s.store === 'epic' || s.store === 'gog'
              return (
                <li
                  key={s.store}
                  className="flex items-center justify-between gap-3 rounded-xl border border-border bg-surface px-4 py-3.5"
                >
                  <div className="min-w-0">
                    <div className="text-sm font-medium text-fg">{s.displayName}</div>
                    <div className="mt-0.5 text-[11px] text-faint">
                      {connected ? (
                        <span className="text-good">Connected</span>
                      ) : clientInstalled ? (
                        'Found'
                      ) : (
                        'Not installed'
                      )}
                    </div>
                  </div>
                  {needsAuth && backendAvailable ? (
                    <button
                      type="button"
                      className="exo-ghost-btn shrink-0"
                      disabled={authBusy === s.store}
                      onClick={() => onAuth(s.store)}
                    >
                      {authBusy === s.store ? (
                        <span className="inline-flex items-center gap-1.5">
                          <Loader2 className="h-3.5 w-3.5 animate-spin" /> Working…
                        </span>
                      ) : accountConnected ? (
                        'Reconnect'
                      ) : (
                        'Connect'
                      )}
                    </button>
                  ) : (
                    <span
                      className={cn(
                        'shrink-0 text-[11px] uppercase tracking-wide',
                        connected ? 'text-good' : 'text-fg-subtle',
                      )}
                    >
                      {connected ? 'Connected' : clientInstalled ? 'Ready' : 'Not installed'}
                    </span>
                  )}
                </li>
              )
            })}
          </ul>

          <div className="mt-8 flex flex-wrap items-center gap-3">
            <button type="button" className="exo-cta h-11 px-6" onClick={onContinue}>
              Open library
            </button>
            <button type="button" className="text-[12px] text-faint hover:text-fg" onClick={onSkip}>
              Skip
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
