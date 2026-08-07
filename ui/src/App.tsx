import { useEffect } from 'react'
import { initHostBridge } from './lib/host'
import { LauncherApp } from './components/LauncherApp'

export default function App() {
  useEffect(() => {
    initHostBridge()
  }, [])

  return <LauncherApp />
}
