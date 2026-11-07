import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { ZbaRequestError, type ZbaClient, type ZbaInstalledPlugins } from '../../api/zba'
import { matchesLifecycleSelection, type PluginLifecyclePreview, type PluginLifecycleSelection } from '../../api/plugin-lifecycle'
import { sameLifecycleManifest } from '../../api/plugin-lifecycle-diff'

interface LifecycleContext {
  client: () => ZbaClient; installed: () => ZbaInstalledPlugins | null; revision: () => string
  canWrite: () => boolean; active: () => boolean; blockedIds: () => string[]
}
/** No writes here. A one-use preview is correlated with both the selected candidate and installed state. */
export function usePluginLifecyclePreview(context: LifecycleContext) {
  const preview = shallowRef<PluginLifecyclePreview | null>(null)
  const loading = ref(false), error = ref(''), acknowledged = ref(false), now = ref(Date.now())
  let generation = 0
  const clock = setInterval(() => { now.value = Date.now() }, 500)
  const expired = computed(() => Boolean(preview.value && Date.parse(preview.value.expiresAt) <= now.value))
  const canConfirm = computed(() => Boolean(preview.value?.canApply && acknowledged.value && !expired.value && !loading.value
    && context.canWrite() && context.active() && preview.value.revision === context.revision() && stateMatches(preview.value)
    && !context.blockedIds().includes(preview.value.pluginId)))
  function invalidate() {
    generation++; preview.value = null; acknowledged.value = false; error.value = ''; loading.value = false
  }
  function stateMatches(result: PluginLifecyclePreview): boolean {
    const state = context.installed()?.plugins.find(item => item.manifest.id === result.pluginId)
    if (!state || !sameLifecycleManifest(result.currentManifest, state.manifest) || result.enabled !== state.enabled
      || result.currentStatus !== state.status || result.restartRequired !== state.restartRequired) return false
    if (state.pendingManifest ? !result.pendingManifest || !sameLifecycleManifest(result.pendingManifest, state.pendingManifest) : result.pendingManifest !== null) return false
    if (result.recovery === 'failed-install') return result.kind === 'uninstall' && !state.installedVersion && !!state.pendingManifest
      && result.targetManifest === null && sameLifecycleManifest(result.currentManifest, state.pendingManifest)
    if (!state.installedVersion) return false
    if (result.kind !== 'rollback') return result.recovery === 'none'
    if (result.recovery === 'failed-pending') return Boolean(state.pendingManifest && result.targetManifest && sameLifecycleManifest(result.targetManifest, state.manifest))
    return state.previousManifest ? Boolean(result.targetManifest && !sameLifecycleManifest(result.currentManifest, result.targetManifest) && sameLifecycleManifest(result.targetManifest, state.previousManifest))
      : result.targetManifest === null && !result.canApply
  }
  async function request(selection: PluginLifecycleSelection) {
    invalidate()
    if (!context.canWrite() || !context.active() || !context.revision() || context.blockedIds().includes(selection.pluginId)) return
    if (!context.installed()?.plugins.some(item => item.manifest.id === selection.pluginId)) {
      error.value = '缺少已安装状态，请先刷新后再预览。'; return
    }
    const token = generation, client = context.client(), expectedRevision = context.revision()
    loading.value = true
    try {
      const result = await client.previewPluginLifecycle({ ...selection, expectedRevision })
      if (generation !== token || !context.active() || !context.canWrite()) return
      if (!matchesLifecycleSelection(result, { ...selection, expectedRevision }) || !stateMatches(result)) {
        error.value = '预览目标、已安装状态或配置代际不一致，请刷新并重新预览。'; return
      }
      now.value = Date.now(); preview.value = result
    } catch (cause) {
      if (token !== generation) return
      error.value = cause instanceof ZbaRequestError ? '无法预览：' + cause.code + '。请刷新并重新预览。'
        : '预览未完成；没有提交变更，可以重新预览。'
    } finally { if (token === generation) loading.value = false }
  }
  function consume(): PluginLifecyclePreview | null {
    now.value = Date.now()
    if (!canConfirm.value) return null
    const result = preview.value
    invalidate()
    return result
  }
  watch(() => [context.client(), context.client().identityLabel, context.revision(), context.canWrite(), context.active(),
    context.blockedIds().join('|'), JSON.stringify(context.installed())], invalidate, { flush: 'sync' })
  watch(expired, value => { if (value) acknowledged.value = false }, { flush: 'sync' })
  onBeforeUnmount(() => { clearInterval(clock); invalidate() })
  return { preview, loading, error, acknowledged, expired, canConfirm, request, invalidate, consume }
}