import { isHost } from '../lib/host'
import { NativeDiscordRoom } from './discord/NativeDiscordRoom'

/** The Discord room is Exo's own Discord client. */
export function FriendsRoom(props: { active: boolean }) {
  // The dev preview shows Exo's own client with made-up servers and people.
  if (!isHost() && !import.meta.env.DEV) return <div className="exo-empty" role="alert"><p>Discord opens inside the Exo app.</p></div>
  return <NativeDiscordRoom active={props.active} />
}
