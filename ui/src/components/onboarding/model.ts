export const STEPS = [
  { id: 'welcome', title: 'Welcome to Exo' },
  { id: 'stores', title: 'Your game stores' },
  { id: 'apps', title: 'Music' },
  { id: 'discord', title: 'Discord' },
  { id: 'capture', title: 'Captures' },
  { id: 'pc', title: 'This PC' },
  { id: 'cleanup', title: 'Clean up' },
] as const

export type StepId = (typeof STEPS)[number]['id']
export type StoreAction = 'connect' | 'open'
export type StoreCheckState = 'checking' | 'ready' | 'error'
export type Feedback = { message: string; error?: boolean }
export type MissingRuntime = { id: string; name: string }
