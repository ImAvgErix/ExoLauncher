import { ExoMark } from '../brand/ExoMark'

export function OnboardingPanel({
  message,
  onOpenLibrary,
  onAddFolder,
}: {
  message: string | null
  onOpenLibrary: () => void
  onAddFolder: () => void
}) {
  return (
    <div className="exo-app">
      <div className="relative z-10 flex min-h-0 flex-1 flex-col items-center justify-center px-10 py-12">
        <div className="w-full max-w-md">
          <ExoMark size={36} />

          <h1 className="mt-8 text-[28px] font-semibold tracking-tight text-fg">Your library, in one place</h1>
          <p className="mt-2 max-w-md text-[13px] leading-relaxed text-faint">Installed games from store apps on this PC, or a folder you add.</p>

          {message && <div className="mt-5 border-l-2 border-line bg-elevated px-4 py-3 text-[13px] text-fg" role="status">{message}</div>}

          <div className="mt-8 flex flex-wrap items-center gap-3">
            <button type="button" className="exo-cta h-11 px-6" onClick={onOpenLibrary}>Open library</button>
            <button type="button" className="exo-ghost-btn h-11 px-4 text-[12px]" onClick={onAddFolder}>Add a folder</button>
          </div>
        </div>
      </div>
    </div>
  )
}
