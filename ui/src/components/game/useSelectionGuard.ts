import { useEffect, useRef } from 'react'

/** Ignore late UI callbacks once their game has changed or the panel has closed. */
export function useSelectionGuard(key: string) {
  const current = useRef(key)
  current.current = key
  const mounted = useRef(true)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  return <T,>(callback: (value: T) => void) => (value: T) => {
    if (mounted.current && current.current === key) callback(value)
  }
}
