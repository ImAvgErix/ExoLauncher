/** A server emoji's picture on Discord's CDN. Its own module, so the top bar's soundboard needn't load chat formatting. */
export function emojiUrl(id: string, animated: boolean, size = 48) {
  return `https://cdn.discordapp.com/emojis/${id}.${animated ? 'gif' : 'webp'}?size=${size}&quality=lossless`
}
