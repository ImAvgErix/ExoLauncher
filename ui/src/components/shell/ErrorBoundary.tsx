import { Component, type ReactNode } from 'react'
import { Button } from '../ui/Button'
import { CopyLogs } from '../ui/ErrorNote'
import { host } from '../../lib/host'
import { report } from '../../lib/report'

type ErrorBoundaryProps = {
  label: string
  children: ReactNode
  onReset?: () => void
  className?: string
}

type ErrorBoundaryState = { failed: boolean; message?: string }

function fallbackBody(label: string): string {
  if (label === 'Exo') return 'Exo hit an error. Try again, or open the logs to see what happened.'
  return `${label} hit an error. The rest of Exo is still running.`
}

/**
 * Contains a render crash to one part of the shell. The rest of Exo keeps
 * working; the failed part shows a quiet card with a way back and a way to the logs.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { failed: false }

  static getDerivedStateFromError(error: unknown): ErrorBoundaryState {
    return { failed: true, message: error instanceof Error ? error.message : String(error) }
  }

  componentDidCatch(error: Error): void {
    report(error, `boundary:${this.props.label}`)
  }

  private reset = () => {
    this.setState({ failed: false })
    this.props.onReset?.()
  }

  private openLogs = () => {
    void host.openLogs().catch((error: unknown) => report(error, 'ErrorBoundary.openLogs'))
  }

  render(): ReactNode {
    if (!this.state.failed) return this.props.children
    return (
      <div className={this.props.className ? `exo-empty ${this.props.className}` : 'exo-empty'} role="alert">
        <h2>This part of Exo stopped</h2>
        <p>{fallbackBody(this.props.label)}</p>
        <div className="flex flex-wrap items-center justify-center gap-2">
          <Button type="button" size="sm" className="exo-set-btn is-primary" onPress={this.reset}>
            Try again
          </Button>
          <Button type="button" size="sm" className="exo-set-btn" onPress={this.openLogs}>
            Open logs
          </Button>
          <CopyLogs error={`${this.props.label} stopped: ${this.state.message ?? 'an error'}`} />
        </div>
      </div>
    )
  }
}
