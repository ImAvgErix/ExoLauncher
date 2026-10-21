export function buyLabel(store: string | null | undefined): string {
  if (store === 'steam') return 'Buy on Steam'
  if (store === 'gog') return 'Buy on GOG'
  return 'Buy in browser'
}
