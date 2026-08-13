import { useEffect, useState } from 'react'
import { Close, Maximize, Minus, Restore } from '../brand/icons'
import { host, onHostEvent } from '../lib/host'

export function WindowChrome() {
  const [maximized, setMaximized] = useState(false)

  useEffect(() => {
    void host.windowState().then((result) => {
      if (typeof result?.maximized === 'boolean') setMaximized(result.maximized)
    }).catch(() => {})
    return onHostEvent('shell.window', (data) => {
      const next = data as { maximized?: boolean }
      if (typeof next?.maximized === 'boolean') setMaximized(next.maximized)
    })
  }, [])

  return (
    <>
      <button type="button" className="exo-winbtn" title="Minimize" onClick={() => void host.minimize()}>
        <Minus />
      </button>
      <button
        type="button"
        className="exo-winbtn"
        title={maximized ? 'Restore' : 'Maximize'}
        onClick={() => {
          void host.maximize().then((result) => {
            if (typeof result?.maximized === 'boolean') setMaximized(result.maximized)
          }).catch(() => {})
        }}
      >
        {maximized ? <Restore /> : <Maximize />}
      </button>
      <button type="button" className="exo-winbtn is-close" title="Close" onClick={() => void host.close()}>
        <Close />
      </button>
    </>
  )
}
