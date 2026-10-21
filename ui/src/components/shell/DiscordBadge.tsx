import { useEffect, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'

/** Discord's unread count on the Friends tab. Only this badge re-renders when it changes. */
export function DiscordBadge() {
  const [count, setCount] = useState(0)
  useEffect(() => {
    let live = true, revision = 0
    const accept = (value: unknown) => {
      if (!live || !value || typeof value !== 'object' || !('count' in value)) return
      const count = value.count
      if (typeof count === 'number' && Number.isFinite(count)) setCount(Math.max(0, Math.floor(count)))
    }
    const unsubscribe = onHostEvent('friends.badge', value => { revision++; accept(value) })
    void host.friendsBadge().then(value => { if (revision === 0) accept(value) }).catch((error: unknown) => report(error, 'DiscordBadge.friendsBadge', { level: 'warn' }))
    return () => { live = false; unsubscribe() }
  }, [])
  if (count <= 0) return null
  return <span className="exo-discord-notification-count" aria-label={`${count} Discord notification${count === 1 ? '' : 's'}`}>{count > 99 ? '99+' : count}</span>
}
