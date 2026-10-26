import type { MissingDependency } from '../../lib/host'
import { useNotice } from '../../lib/notify'
import { WHATS_NEW } from '../../lib/whatsNew'
import type { AppUpdate } from './useAppUpdate'
import type { StatusTone } from './useActionStatus'

export type DepPrompt = {
  gameId: string
  action: 'play' | 'install' | 'update'
  deps: MissingDependency[]
  awaitingContinue?: boolean
}

export type ShellNoticesProps = {
  depPrompt: DepPrompt | null
  onSkipDeps: () => void
  onInstallDeps: () => void
  onContinueAfterDeps: () => void
  onDismissDeps: () => void
  update: AppUpdate
  /** The update notice hides on the Settings room, which has its own control. */
  showUpdate: boolean
  /** A message that belongs to no open sheet. Only failures read red; a sticky one waits for Dismiss. */
  notice: { message: string; tone: StatusTone; sticky: boolean } | null
  onDismissNotice: () => void
  /** A passing shell notice (a sale, a mouse alert), shown beside the status, never over it. */
  passing?: string | null
  transferFailures?: Array<{ gameId: string; title: string; message: string }>
  onDismissTransferFailure?: (gameId: string) => void
}

/** What the shell's state has to say, as notices in the one stack (shell/NoticeStack.tsx). Draws nothing itself. */
export function ShellNotices({
  depPrompt, onSkipDeps, onInstallDeps, onContinueAfterDeps, onDismissDeps,
  update, showUpdate, notice, onDismissNotice, passing = null,
  transferFailures = [], onDismissTransferFailure,
}: ShellNoticesProps) {
  useDepsNotice(depPrompt, onSkipDeps, onInstallDeps, onContinueAfterDeps, onDismissDeps)
  useUpdateNotice(update, showUpdate)
  useNotice('shell:status', notice && {
    text: notice.message,
    tone: notice.tone === 'bad' ? 'bad' : undefined,
    sticky: notice.sticky,
    onDismiss: onDismissNotice,
  })
  useNotice('shell:passing', Boolean(passing) && passing !== notice?.message && { text: passing ?? '', sticky: false })
  return <>{transferFailures.map(failure => (
    <TransferFailure key={failure.gameId} failure={failure} onDismiss={() => onDismissTransferFailure?.(failure.gameId)} />
  ))}</>
}

function TransferFailure({ failure, onDismiss }: { failure: { gameId: string; title: string; message: string }; onDismiss: () => void }) {
  useNotice(`shell:transfer:${failure.gameId}`, { title: failure.title, text: failure.message, tone: 'bad', onDismiss })
  return null
}

function useDepsNotice(prompt: DepPrompt | null, onSkip: () => void, onInstall: () => void, onContinue: () => void, onDismiss: () => void) {
  const names = prompt?.deps.map(dep => dep.name).join(', ')
  useNotice('shell:deps', prompt && {
    title: prompt.awaitingContinue ? `Waiting for ${names}` : `Needs ${names}`,
    // Installed in one go, from the makers' own installers; Windows asks once. Only a restart Windows wants makes it wait.
    text: prompt.awaitingContinue ? 'Restart Windows to finish installing, then check again.'
      : prompt.deps.length > 1 ? 'This game needs them to run. Exo installs them in one go.' : 'This game needs it to run. Exo can install it now.',
    actions: [
      { label: 'Try without', onPress: onSkip },
      prompt.awaitingContinue
        ? { label: 'Check and retry', onPress: onContinue, primary: true }
        : { label: 'Install', onPress: onInstall, primary: true },
    ],
    onDismiss,
  })
}

function useUpdateNotice(update: AppUpdate, shown: boolean) {
  const percent = typeof update.percent === 'number' && Number.isFinite(update.percent) ? Math.min(100, Math.max(0, update.percent)) / 100 : null
  const offer = [
    ...(update.upcoming ? [{ label: "What's new", onPress: () => window.dispatchEvent(new Event(WHATS_NEW)) }] : []),
    { label: 'Install update', onPress: () => void update.install(), primary: true },
  ]
  useNotice('shell:update', Boolean(update.banner && shown) && {
    text: update.busy ? update.status || 'Installing update…' : update.banner ?? '',
    tone: update.error && !update.busy ? 'bad' : undefined,
    progress: update.busy ? percent : undefined,
    actions: update.busy ? [] : offer,
    lasting: update.busy,
    onDismiss: update.dismiss,
  })
}
