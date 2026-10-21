// Writes src/components/discord/emoji.json, the Discord room's Unicode emoji: Discord's categories and
// order, each emoji fully qualified with its shortcodes (Discord's names first: JoyPixels', which Discord
// uses, then GitHub's and Slack's as aliases) and a few words to find it by. Emoji newer than 15.0 are left
// out (Windows' emoji font and Discord's reactions may not know them yet).
//
// Data: emojibase-data 17.0.0 (MIT, see THIRD_PARTY_NOTICES.md), unpacked somewhere outside the repo:
//   npm pack emojibase-data@17.0.0 && tar -xzf emojibase-data-17.0.0.tgz
//   node scripts/build-emoji.mjs <that folder>/package
import { readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

const root = process.argv[2]
if (!root) throw new Error('Usage: node scripts/build-emoji.mjs <emojibase-data folder>')
const read = file => JSON.parse(readFileSync(join(root, file), 'utf8'))
const data = read('en/data.json')
const sets = ['joypixels', 'github', 'iamcal'].map(name => read(`en/shortcodes/${name}.json`))

// emojibase group -> Discord's category, in Discord's order.
const CATEGORIES = [
  ['people', 'People', [0, 1]], ['nature', 'Nature', [3]], ['food', 'Food', [4]], ['activities', 'Activities', [6]],
  ['travel', 'Travel', [5]], ['objects', 'Objects', [7]], ['symbols', 'Symbols', [8]], ['flags', 'Flags', [9]],
]

// Fully qualified, as Discord stores it: VS16 after a code point that isn't emoji-styled by default
// (not before a skin tone, not after a keycap's base that already has it).
function qualified(hexcode) {
  const points = hexcode.split('-').map(h => parseInt(h, 16)).filter(p => p !== 0xfe0f)
  let out = ''
  points.forEach((point, i) => {
    const char = String.fromCodePoint(point)
    out += char
    const next = points[i + 1]
    const modifier = next !== undefined && next >= 0x1f3fb && next <= 0x1f3ff
    if (/\p{Emoji}/u.test(char) && !/\p{Emoji_Presentation}/u.test(char) && !/\p{Emoji_Modifier}/u.test(char) && !modifier) out += '️'
  })
  return out
}

// Where Discord's own emoji list (its web client's emoji data) differs: these it keeps without VS16, and a
// reaction sent with it is refused as Unknown Emoji; these it names otherwise, and its favorites and
// frecency settings key a Unicode emoji by that first name.
const DISCORD_BARE = new Set(['00A9', '00AE', '2122', '26A7', '1F441-FE0F-200D-1F5E8-FE0F'])
const DISCORD_NAMES = { '1FA85': 'piñata', '1F6B1': 'non_potable_water', '1F471-200D-2640-FE0F': 'blond_haired_woman', '1F471-200D-2642-FE0F': 'blond_haired_man', '1F4E7': 'e_mail', '269C': 'fleur_de_lis' }
const surrogates = hexcode => DISCORD_BARE.has(hexcode) ? hexcode.split('-').filter(h => h !== 'FE0F').map(h => String.fromCodePoint(parseInt(h, 16))).join('') : qualified(hexcode)
const names = hexcode => [...new Set([DISCORD_NAMES[hexcode] ?? [], ...sets.map(set => set[hexcode] ?? [])].flat())]
const categories = CATEGORIES.map(([id, name, groups]) => {
  const emoji = data
    .filter(e => groups.includes(e.group) && e.version <= 15)
    .sort((a, b) => a.order - b.order)
    .map(e => {
      const codes = names(e.hexcode)
      if (codes.length === 0) return null
      const words = [...new Set((e.tags ?? []).filter(tag => !codes.includes(tag)))]
      return `${surrogates(e.hexcode)}|${codes.join(' ')}|${words.join(' ')}`
    })
    .filter(Boolean)
  return { id, name, emoji }
})
const out = new URL('../src/components/discord/emoji.json', import.meta.url)
writeFileSync(out, JSON.stringify(categories) + '\n')
console.log(categories.map(c => `${c.name} ${c.emoji.length}`).join(', '))
