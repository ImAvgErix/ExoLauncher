import { useEffect, useState } from 'react'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { useMenuArrowKeys } from '../../lib/menuKeys'
import type { DiscordState } from '../../lib/discord'
import { DiscordClient } from './DiscordClient'
import { DiscordQrSignIn } from './DiscordSignIn'

/**
 * The Discord room as Exo's own client. Signed out it
 * shows a QR code for Discord's phone app; connected, the client itself.
 */
export function NativeDiscordRoom({ active }: { active: boolean }) {
  // Every menu in the client takes Up, Down, Home and End.
  useMenuArrowKeys('.exo-dc')
  const [state, setState] = useState<DiscordState | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  // Once connected, a reconnect keeps the client on screen (where you are, drafts, scroll); Discord's own does the same.
  const [wasReady, setWasReady] = useState(false)
  if (state === 'ready' && !wasReady) setWasReady(true)
  if ((state === 'signedOut' || state === 'error') && wasReady) setWasReady(false)

  useEffect(() => {
    let live = true
    const off = onHostEvent('discord.state', event => { setState(event.state); setMessage(event.message ?? null) })
    host.discordStatus().then(status => {
      if (!live) return
      setState(status.state === 'off' && status.signedIn ? 'connecting' : status.state)
      setMessage(status.message ?? null)
    }).catch((error: unknown) => {
      report(error, 'NativeDiscordRoom.status')
      if (live) setState('error')
    })
    return () => { live = false; off() }
  }, [])

  const retry = () => {
    setState('connecting')
    host.discordStatus(true).then(status => {
      setState(status.state === 'off' && status.signedIn ? 'connecting' : status.state)
      setMessage(status.message ?? null)
    }).catch((error: unknown) => {
      report(error, 'NativeDiscordRoom.retry')
      setState('error')
    })
  }

  if (state === 'ready' || (wasReady && state === 'connecting')) return <DiscordClient active={active} />
  if (state === 'signedOut') return <SignIn note={message} />
  return (
    <main className="exo-hosted-room">
      <div className="exo-hosted-room-state" role={state === 'error' ? 'alert' : 'status'}>
        <p>{state === 'error' ? message ?? 'Discord could not be reached.' : 'Connecting to Discord'}</p>
        {state === 'error' ? <button type="button" className="exo-set-btn is-primary" onClick={retry}>Try again</button> : null}
      </div>
    </main>
  )
}

function SignIn({ note }: { note: string | null }) {
  return (
    <main className="exo-dc-signin" aria-label="Sign in to Discord">
      <div className="exo-dc-signin-body exo-motion-arrive">
        <CompanyLogo company="discord" size={48} />
        <h1>Sign in with your phone</h1>
        <p>Open Discord on your phone, go to you › scan QR code, and point it here.</p>
        <DiscordQrSignIn note={note} />
        <p className="exo-dc-signin-note">Exo never sees your password. Discord's terms don't allow other clients; you use this at your own risk.</p>
      </div>
    </main>
  )
}
