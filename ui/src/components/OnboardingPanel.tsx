import { ExoMark } from '../brand/ExoMark'
import type { StoreStatus } from '../lib/host'

export function OnboardingPanel({
  stores,
  message,
  onContinue,
  onSkip,
}: {
  stores: StoreStatus[]
  message: string | null
  onContinue: () => void
  onSkip: () => void
}) {
  const rows = stores.filter((store) => store.store !== 'local' && store.clientPresent === true)

  return (
    <div className="exo-app">
      <div className="relative z-10 flex min-h-0 flex-1 flex-col items-center justify-center px-10 py-12">
        <div className="w-full max-w-lg">
          <div className="mb-8 flex items-center gap-3">
            <ExoMark size={36} />
            <span className="text-lg font-semibold tracking-tight">Exo</span>
          </div>

          <h1 className="text-[28px] font-semibold tracking-tight text-fg">Your library, in one place</h1>
          <p className="mt-2 max-w-md text-[13px] leading-relaxed text-faint">Exo finds installed store apps and keeps your library local.</p>

          {message && <div className="mt-5 border-l-2 border-line bg-elevated px-4 py-3 text-[13px] text-fg" role="status">{message}</div>}

          <ul className="mt-7 grid grid-cols-2 gap-x-6">
            {rows.map((store) => {
              const clientInstalled = store.clientPresent ?? store.agentPresent
              return (
                <li key={store.store} className="flex items-center justify-between gap-3 border-t border-line-soft py-3">
                  <span className="text-sm font-medium text-fg">{store.displayName}</span>
                  <span className={clientInstalled ? 'text-[11px] text-good' : 'text-[11px] text-faint'}>{clientInstalled ? 'Installed' : 'Not installed'}</span>
                </li>
              )
            })}
          </ul>

          <div className="mt-8 flex flex-wrap items-center gap-3">
            <button type="button" className="exo-cta h-11 px-6" onClick={onContinue}>Open library</button>
            <button type="button" className="text-[12px] text-faint hover:text-fg" onClick={onSkip}>Skip</button>
          </div>
        </div>
      </div>
    </div>
  )
}
