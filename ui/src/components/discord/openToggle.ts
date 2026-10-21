/**
 * A press on the button that opened a popover (pressed or expanded) is that button's to handle: its
 * click closes the popover. Closing on the press as well would close it and the click open it again.
 */
export function openToggle(target: EventTarget | null) {
  return target instanceof Element && target.closest('[aria-pressed="true"], [aria-expanded="true"]') !== null
}
