/** Discord's Unicode emoji (emoji.json, written by scripts/build-emoji.mjs), loaded the first time a picker or a : needs them. */
export type UnicodeEmoji = { emoji: string; name: string; names: string[]; words: string[] }
export type EmojiCategory = { id: string; name: string; emoji: UnicodeEmoji[] }
export type UnicodeEmojiSet = { categories: EmojiCategory[]; byName: Map<string, string> }

let loading: Promise<UnicodeEmojiSet> | null = null

export function loadUnicodeEmoji(): Promise<UnicodeEmojiSet> {
  loading ??= import('./emoji.json').then(module => {
    const categories = (module.default as { id: string; name: string; emoji: string[] }[]).map(category => ({
      id: category.id,
      name: category.name,
      emoji: category.emoji.map(line => {
        const [emoji, names, words] = line.split('|')
        const list = names.split(' ')
        return { emoji, name: list[0], names: list, words: words ? words.toLowerCase().split(' ') : [] }
      }),
    }))
    const byName = new Map<string, string>()
    for (const category of categories) for (const e of category.emoji) for (const name of e.names) if (!byName.has(name)) byName.set(name, e.emoji)
    return { categories, byName }
  })
  loading.catch(() => { loading = null })
  return loading
}

/** Emoji whose names start with the text first, then names and words that have it; at most limit. */
export function findUnicodeEmoji(set: UnicodeEmojiSet, query: string, limit: number): UnicodeEmoji[] {
  const q = query.toLowerCase()
  const starts: UnicodeEmoji[] = []
  const has: UnicodeEmoji[] = []
  for (const category of set.categories) {
    for (const e of category.emoji) {
      if (e.names.some(n => n.startsWith(q))) starts.push(e)
      else if (e.names.some(n => n.includes(q)) || e.words.some(w => w.startsWith(q))) has.push(e)
    }
  }
  return [...starts, ...has].slice(0, limit)
}

/**
 * :smile: typed in a message becomes 😄, and a server emoji's :name: becomes the emoji where it can be sent,
 * as Discord's client sends them; never inside code, never inside a server emoji already written out.
 */
export function shortcodesToEmoji(text: string, byName: Map<string, string> | null, custom: Map<string, string>): string {
  if (!text.includes(':')) return text
  return text.split(/(```[\s\S]*?```|`[^`\n]*`)/).map((part, i) => i % 2 ? part
    : part.replace(/(?<!<a?):([\w+-]{2,32}):(?!\d{5,20}>)/g, (whole, name: string) => custom.get(name) ?? byName?.get(name) ?? whole)).join('')
}
