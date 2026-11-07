import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { ZbaClient, ZbaRequestError } from '../../api/zba'
import type { PluginInstallPreview, PluginPreviewSelection } from '../../api/plugin-import'
import { samePreviewManifest } from './plugin-preview-candidate'

interface PreviewContext {
  client: () => ZbaClient; revision: () => string; canWrite: () => boolean; active: () => boolean; blockedIds: () => string[]
}
/** Owns one disposable read-only preview. Only consume() can authorize its exact candidate. */
export function usePluginPreview(context: PreviewContext) {
  const preview = shallowRef<PluginInstallPreview | null>(null)
  const loading = ref(false)
  const error = ref('')
  const acknowledged = ref(false)
  const now = ref(Date.now())
  let generation = 0
  const clock = setInterval(() => { now.value = Date.now() }, 500)
  const expired = computed(() => Boolean(preview.value && Date.parse(preview.value.expiresAt) <= now.value))
  const canConfirm = computed(() => Boolean(preview.value?.canInstall && acknowledged.value && !expired.value && !loading.value
    && context.active() && context.canWrite() && preview.value.revision === context.revision()
    && !context.blockedIds().includes(preview.value.manifest.id)))
  function invalidate() {
    generation++; preview.value = null; acknowledged.value = false; error.value = ''; loading.value = false
  }
  async function request(selection: PluginPreviewSelection) {
    invalidate()
    if (!context.canWrite() || !context.active() || !context.revision()) return
    const token = generation
    const client = context.client()
    const expectedRevision = context.revision()
    const candidate = selection.origin === 'repository' ? { id: selection.pluginId, version: selection.version } : selection.manifest
    loading.value = true
    try {
      const result = await client.previewPlugin({ ...selection, expectedRevision })
      if (token !== generation || !context.canWrite() || !context.active()) return
      if (result.revision !== expectedRevision || result.source.kind !== selection.origin
        || result.manifest.id !== candidate.id || result.manifest.version !== candidate.version
        || (selection.origin !== 'repository' && !samePreviewManifest(result.manifest, selection.manifest))) {
        error.value = '预览候选或配置代际已变化，请重新预览。'; return
      }
      now.value = Date.now()
      preview.value = result
    } catch (cause) {
      if (token !== generation) return
      error.value = cause instanceof ZbaRequestError
        ? '无法预览：' + cause.code + '。请核对来源并重新预览。'
        : '预览未完成；没有安装或下载插件，可以重新预览。'
    } finally { if (token === generation) loading.value = false }
  }
  function consume(): PluginInstallPreview | null {
    now.value = Date.now()
    if (!canConfirm.value) return null
    const result = preview.value
    invalidate()
    return result
  }
  watch(() => [context.client(), context.client().identityLabel, context.revision(), context.canWrite(), context.active(), context.blockedIds().join('|')], invalidate, { flush: 'sync' })
  watch(expired, value => { if (value) acknowledged.value = false })
  onBeforeUnmount(() => { clearInterval(clock); invalidate() })
  return { preview, loading, error, acknowledged, expired, canConfirm, request, invalidate, consume }
}