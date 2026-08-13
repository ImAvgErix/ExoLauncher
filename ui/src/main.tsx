import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@fontsource-variable/geist'
import '@fontsource-variable/geist-mono'
import './exo-shell.css'
import './tokens.css'
import { initHostBridge } from './lib/host'
import App from './App'

// Attach the WebView message listener before any child effect can post RPCs.
// React runs child useEffects first; deferring init to App caused dropped
// settings/library responses on cold start.
initHostBridge()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
