/** Titlebar avatar: never treat a stripped profile read as "no art". */

export type TitlebarIdentityCurrent = {
  avatarGameId: string | null
  avatarImageUrl: string | null
}

export type TitlebarIdentityApplied = {
  name: string | null
  avatarGameId: string | null
  avatarImageUrl: string | null
  cacheable: boolean
}

export function applyTitlebarIdentity(
  incoming: {
    ok?: boolean
    name?: string | null
    avatarGameId?: string | null
    avatarImageUrl?: string | null
  },
  current: TitlebarIdentityCurrent,
  libraryReady: boolean,
): TitlebarIdentityApplied | null {
  if (!incoming?.ok) return null
  const nextGame = trimOrNull(incoming.avatarGameId)
  const nextImage = trimOrNull(incoming.avatarImageUrl)
  const stripped = !nextGame && !nextImage && !libraryReady
  return {
    name: incoming.name ?? null,
    avatarGameId: nextGame ?? (libraryReady ? null : current.avatarGameId),
    avatarImageUrl: nextImage ?? (nextGame ? null : libraryReady ? null : current.avatarImageUrl),
    cacheable: !stripped,
  }
}

function trimOrNull(value?: string | null): string | null {
  const trimmed = value?.trim()
  return trimmed ? trimmed : null
}
