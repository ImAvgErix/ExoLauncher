import { useEffect } from 'react'

const ROWS = '[role="menuitem"], [role="menuitemcheckbox"], [role="menuitemradio"]'
const KEYS = new Set(['ArrowDown', 'ArrowUp', 'Home', 'End'])

/** The rows an arrow key reaches: the menu's own, in order, not a disabled one or one in a menu opened inside it. */
function rowsOf(menu: Element) {
  return Array.from(menu.querySelectorAll<HTMLElement>(ROWS))
    .filter(row => row.closest('[role="menu"]') === menu && !(row as HTMLButtonElement).disabled && row.getClientRects().length > 0)
}

/** Where a key leads: Home and End to the ends, Up and Down one row (from outside the rows, to the first or last). */
function target(key: string, at: number, count: number) {
  if (key === 'Home') return 0
  if (key === 'End') return count - 1
  if (at < 0) return key === 'ArrowDown' ? 0 : count - 1
  return at + (key === 'ArrowDown' ? 1 : -1)
}

/** The menu of `root` a key was pressed in, unless a field in it keeps the key. */
function menuOf(event: KeyboardEvent, root: string) {
  const from = event.target instanceof Element ? event.target : null
  if (!from || from.closest('input, textarea, select, [contenteditable="true"]')) return null
  const menu = from.closest('[role="menu"]')
  return menu?.closest(root) ? menu : null
}

/**
 * Up, Down, Home and End walk the rows of any menu in `root`, the way a menu works everywhere else: one handler for every menu of
 * Exo's Discord client, after a menu's own keys had their turn (one that already acted left the event prevented).
 */
export function useMenuArrowKeys(root: string) {
  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || !KEYS.has(event.key)) return
      const menu = menuOf(event, root)
      const rows = menu ? rowsOf(menu) : []
      if (!menu || rows.length === 0) return
      const row = (event.target as Element).closest<HTMLElement>(ROWS)
      const to = target(event.key, row ? rows.indexOf(row) : -1, rows.length)
      event.preventDefault()
      rows[(to + rows.length) % rows.length]?.focus()
    }
    document.addEventListener('keydown', key)
    return () => document.removeEventListener('keydown', key)
  }, [root])
}
