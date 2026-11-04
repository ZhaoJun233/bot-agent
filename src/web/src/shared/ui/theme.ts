import { ref } from 'vue'

export type Theme = 'light' | 'dark'
const storageKey = 'botagent-appearance'
const theme = ref<Theme>('light')
let initialized = false

/** Appearance is the only persisted preference; session credentials stay in memory. */
export function useTheme() {
  function apply(value: Theme): void {
    theme.value = value
    if (typeof document !== 'undefined') document.documentElement.dataset.theme = value
  }
  if (!initialized) {
    initialized = true
    try {
      const saved = localStorage.getItem(storageKey)
      apply(saved === 'dark' ? 'dark' : 'light')
    } catch { apply('light') }
  }
  function toggle(): void {
    apply(theme.value === 'light' ? 'dark' : 'light')
    try { localStorage.setItem(storageKey, theme.value) } catch { /* Storage can be unavailable. */ }
  }
  return { theme, toggle }
}
