import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './tokens.css'
import './exo-shell.css'
import './styles/rooms.css'
import './styles/hosted-room.css'
import { host, initHostBridge } from './lib/host'
import { installGlobalErrorReporting, setReportSink } from './lib/report'
import { ErrorBoundary } from './components/shell/ErrorBoundary'
import { installFocusKeeper } from './lib/focusKeeper'
import { installMenuFit } from './lib/menuFit'
import { installInputModality } from './lib/motionPreference'
import App from './App'

// Attach the WebView message listener before any child effect can post RPCs.
// React runs child useEffects first; deferring init to App caused dropped
// settings/library responses on cold start.
initHostBridge()
setReportSink(host.log)
installGlobalErrorReporting()
installFocusKeeper()
installMenuFit()
installInputModality()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary label="Exo">
      <App />
    </ErrorBoundary>
  </StrictMode>,
)
