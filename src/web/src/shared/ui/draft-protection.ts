import { onBeforeUnmount, onMounted } from 'vue'

/** Browser-owned warning only. Never persist drafts or secrets to browser storage. */
export function useDraftProtection(hasUnsavedWork: () => boolean): void {
  function beforeUnload(event: BeforeUnloadEvent): void {
    if (!hasUnsavedWork()) return
    event.preventDefault()
    event.returnValue = ''
  }
  onMounted(() => window.addEventListener('beforeunload', beforeUnload))
  onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
}
