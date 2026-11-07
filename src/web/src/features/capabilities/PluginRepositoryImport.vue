<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { ZbaClient, ZbaRequestError } from '../../api/zba'
import type { PluginPreviewSelection, PluginSourceDiscovery } from '../../api/plugin-import'
import { githubSourceError } from './plugin-manifest'
import { parsePluginJson } from './plugin-json-import'
const props = defineProps<{ client: ZbaClient; canWrite: boolean; active: boolean; busy: boolean }>()
const emit = defineEmits<{ preview: [selection: PluginPreviewSelection]; invalidate: []; 'draft-change': [dirty: boolean] }>()
const sourceUrl = ref('')
const discovery = shallowRef<PluginSourceDiscovery | null>(null)
const sourceBusy = ref(false)
const sourceError = ref('')
const pluginId = ref('')
const version = ref('')
const json = ref('')
const jsonError = ref('')
const now = ref(Date.now())
const clock = setInterval(() => { now.value = Date.now() }, 500)
let generation = 0
const candidates = computed(() => discovery.value?.candidates ?? [])
const plugins = computed(() => [...new Map(candidates.value.map(candidate => [candidate.pluginId, candidate])).values()])
const versions = computed(() => candidates.value.filter(candidate => candidate.pluginId === pluginId.value))
const expired = computed(() => Boolean(discovery.value && Date.parse(discovery.value.expiresAt) <= now.value))
function invalidate() { generation++; sourceBusy.value = false; discovery.value = null; sourceError.value = ''; pluginId.value = ''; version.value = ''; emit('invalidate') }
watch(sourceUrl, invalidate, { flush: 'sync' })
watch(pluginId, () => { version.value = versions.value[0]?.version ?? ''; emit('invalidate') }, { flush: 'sync' })
watch(version, () => emit('invalidate'), { flush: 'sync' })
watch(json, () => { jsonError.value = ''; emit('invalidate') }, { flush: 'sync' })
watch(() => [props.client, props.client.identityLabel, props.canWrite, props.active], () => { invalidate(); json.value = '' }, { flush: 'sync' })
async function discover() {
  if (!props.canWrite || !props.active || props.busy || sourceBusy.value) return
  invalidate()
  const invalid = githubSourceError(sourceUrl.value.trim())
  if (invalid) { sourceError.value = invalid; return }
  const token = generation
  sourceBusy.value = true
  try {
    const result = await props.client.discoverPluginSource(sourceUrl.value.trim())
    if (token !== generation || !props.canWrite || !props.active) return
    discovery.value = result
    pluginId.value = result.candidates[0]?.pluginId ?? ''
    version.value = versions.value[0]?.version ?? ''
    now.value = Date.now()
  } catch (cause) {
    if (token === generation) sourceError.value = cause instanceof ZbaRequestError ? '来源发现失败：' + cause.code : '无法读取仓库；没有下载或安装插件。'
  } finally { if (token === generation) sourceBusy.value = false }
}
function previewRepository() {
  if (!props.canWrite || !props.active || props.busy || !discovery.value || Date.parse(discovery.value.expiresAt) <= Date.now()
    || !versions.value.some(candidate => candidate.version === version.value)) return
  emit('preview', { origin: 'repository', discoveryId: discovery.value.discoveryId, pluginId: pluginId.value, version: version.value })
}
function previewJson() {
  if (!props.canWrite || !props.active || props.busy) return
  try { emit('preview', { origin: 'json', manifest: parsePluginJson(json.value) }) }
  catch (cause) { jsonError.value = cause instanceof Error ? cause.message : '清单无效。' }
}
watch([sourceUrl, json], () => emit('draft-change', Boolean(sourceUrl.value.trim() || json.value.trim())), { flush: 'sync' })
defineExpose({ reset: () => { sourceUrl.value = ''; json.value = ''; invalidate() } })
onBeforeUnmount(() => { clearInterval(clock); invalidate() })
</script>
<template>
  <details open class="source-panel" aria-labelledby="marketplace-source-heading">
    <summary id="marketplace-source-heading">从 GitHub 仓库导入</summary>
    <p>先发现仓库中的插件与版本，再预览精确候选。此步骤不会下载 ZIP、安装或执行插件。</p>
    <div class="source-row"><label class="grow">公开仓库或固定 Release 标签地址<input v-model="sourceUrl" data-testid="marketplace-source-input" type="url" autocomplete="off" spellcheck="false" placeholder="https://github.com/example-org/example-plugin"></label><button type="button" data-testid="marketplace-source-resolve" :disabled="sourceBusy || props.busy || !props.canWrite" @click="discover">{{ sourceBusy ? '发现中…' : '发现插件与版本' }}</button></div>
    <p v-if="sourceError" class="error" role="alert" data-testid="marketplace-source-error">{{ sourceError }}</p>
    <div v-if="discovery" class="candidate-panel" data-testid="source-candidates">
      <p class="muted">已固定源码版本 <code>{{ discovery.sourceRevision }}</code>。发现结果有时限；地址改变后必须重新发现。</p>
      <div class="source-row"><label class="grow">选择插件<select v-model="pluginId" data-testid="source-plugin"><option v-for="candidate in plugins" :key="candidate.pluginId" :value="candidate.pluginId">{{ candidate.metadata?.name || candidate.pluginId }} · {{ candidate.pluginId }}</option></select></label><label class="grow">选择版本<select v-model="version" data-testid="source-version"><option v-for="candidate in versions" :key="candidate.version" :value="candidate.version">{{ candidate.version }}</option></select></label></div>
      <p v-if="expired" class="error" role="status">发现结果已过期，请重新发现。</p>
      <button type="button" data-testid="source-preview" :disabled="props.busy || !props.canWrite || expired || !version" @click="previewRepository">预览选中的插件与版本</button>
    </div>
    <details class="json-import"><summary>高级：粘贴技术清单 JSON</summary><p>只接受 camelCase 技术清单。不能通过 JSON 指定官方来源、运行时或审核标记；仍须服务端预览与明确确认。</p><label>技术清单<textarea v-model="json" data-testid="marketplace-json-input" rows="8" spellcheck="false" placeholder="粘贴包含 id、version、sha256 等字段的技术清单"></textarea></label><p v-if="jsonError" role="alert" class="error" data-testid="marketplace-json-error">{{ jsonError }}</p><button type="button" data-testid="json-preview" :disabled="props.busy || !props.canWrite || !json.trim()" @click="previewJson">预览 JSON 清单</button></details>
  </details>
</template>
<style scoped>
.source-panel { border: 1px solid var(--border); border-radius: 10px; padding: 1rem; margin-top: 1rem; }summary { cursor: pointer; font-weight: 600; min-height: 2rem; }p { font-size: .85rem; line-height: 1.65; overflow-wrap: anywhere; }.source-row { display: flex; align-items: end; flex-wrap: wrap; gap: .75rem; }.grow { flex: 1 1 12rem; min-width: 0; }label { display: grid; gap: .4rem; font-size: .85rem; }input, select, textarea { width: 100%; box-sizing: border-box; min-height: 44px; color: var(--text); background: var(--surface); border: 1px solid var(--border); border-radius: 7px; padding: .65rem; }textarea { resize: vertical; font-family: monospace; }button { min-height: 44px; padding: .6rem .9rem; border-radius: 7px; border: 1px solid var(--border); background: var(--surface-muted); color: var(--text); }button:disabled { opacity: .5; }.candidate-panel { margin-top: 1rem; }.candidate-panel > button { margin-top: .8rem; }.error { color: var(--danger, var(--warn-text)); }.muted { color: var(--text-muted); }code { word-break: break-all; }.json-import { border-top: 1px solid var(--border); padding-top: .8rem; margin-top: 1.2rem; }input:focus-visible,select:focus-visible,textarea:focus-visible,button:focus-visible,summary:focus-visible { outline: 2px solid var(--accent-deep); outline-offset: 3px; }
</style>