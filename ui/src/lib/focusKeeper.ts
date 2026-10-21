/**
 * Many controls disable themselves while their action runs (Check for updates,
 * a music service being saved). Chromium then drops focus to <body>, so a
 * keyboard or screen-reader user loses their place and arrow keys in a radio
 * group stop working. When the control that held focus comes back enabled and
 * focus is still nowhere, it gets focus back.
 */
export function installFocusKeeper(): void {
  let focused: Element | null = null
  let parked: Element | null = null
  const isControl = (node: unknown): node is HTMLButtonElement | HTMLInputElement =>
    node instanceof HTMLButtonElement || node instanceof HTMLInputElement
  document.addEventListener('focusin', event => { focused = event.target instanceof Element ? event.target : null; parked = null })
  // A blur caused by disabling keeps the control parked; any other blur (Tab, a
  // click elsewhere, the window losing focus) is the user moving on.
  document.addEventListener('focusout', event => {
    if (isControl(event.target) && event.target.disabled) parked = event.target
    else { focused = null; parked = null }
  })
  new MutationObserver(records => {
    for (const { target } of records) {
      if (!isControl(target)) continue
      if (target.disabled) {
        if (target === focused) parked = target
      } else if (target === parked) {
        parked = null
        if (target.isConnected && document.activeElement === document.body) target.focus({ preventScroll: true })
      }
    }
  }).observe(document.body, { subtree: true, attributes: true, attributeFilter: ['disabled'] })
}
