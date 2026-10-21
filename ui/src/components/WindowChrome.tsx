import { useEffect, useState } from 'react'
import { Button } from './ui/Button'
import { Close, Maximize, Minus, Restore } from '../brand/icons'
import { host, onHostEvent } from '../lib/host'

export function WindowChrome() {
  const [maximized, setMaximized] = useState(false)
  // The maximize button is Windows' own maximize region (snap layouts), so
  // the page never sees the pointer over it; the host reports the hover.
  const [maximizeHover, setMaximizeHover] = useState(false)

  useEffect(() => {
    void host.windowState().then((result) => {
      if (typeof result?.maximized === 'boolean') setMaximized(result.maximized)
    }).catch(() => {})
    return onHostEvent('shell.window', (data) => {
      const next = data as { maximized?: boolean; maximizeHover?: boolean }
      if (typeof next?.maximized === 'boolean') setMaximized(next.maximized)
      setMaximizeHover(next?.maximizeHover === true)
    })
  }, [])

  return (
    <>
      {/* No `title`: the user does not want native Windows tooltips anywhere. */}
      <Button type="button" variant="ghost" size="sm" isIconOnly className="exo-winbtn exo-titlebar-button" aria-label="Minimize" onPress={() => void host.minimize()}>
        <Minus />
      </Button>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        isIconOnly
        data-exo-maximize=""
        className={`exo-winbtn exo-titlebar-button${maximizeHover ? ' is-hover' : ''}`}
        aria-label={maximized ? 'Restore' : 'Maximize'}
        onPress={() => {
          void host.maximize().then((result) => {
            if (typeof result?.maximized === 'boolean') setMaximized(result.maximized)
          }).catch(() => {})
        }}
      >
        {maximized ? <Restore /> : <Maximize />}
      </Button>
      <Button type="button" variant="ghost" size="sm" isIconOnly className="exo-winbtn exo-titlebar-button is-close" aria-label="Close" onPress={() => void host.close()}>
        <Close />
      </Button>
    </>
  )
}
