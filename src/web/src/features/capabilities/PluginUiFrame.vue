<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaRequestError, type ZbaClient } from '../../api/zba'
import { isSafePluginUiEntryUrl, type ZbaPluginUiSession } from '../../api/plugin-ui'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { useTheme } from '../../shared/ui/theme'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { createIframeBridgeController } from './plugin-ui-iframe-bridge'

const props = withDefaults(defineProps<{
  client: ZbaClient
  pluginId: string
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active?: boolean
  theme?: 'light' | 'dark'
  currentGenerationId?: string
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
}>(), { active: true, theme: undefined, trigger: null, fallback: null })

const emit = defineEmits<{
  close: []
  saved: []
  'auth-expired': [error: UiError]
}>()

const { theme: appTheme } = useTheme()
const effectiveTheme = computed<'light' | 'dark'>(() => props.theme ?? appTheme.value)

const loading = ref(true)
const error = ref<UiError | null>(null)
const session = ref<ZbaPluginUiSession | null>(null)
const iframeEl = ref<HTMLIFrameElement | null>(null)
let port1: MessagePort | null = null
let controller: ReturnType<typeof createIframeBridgeController> | null = null
let sessionClient: ZbaClient | null = null
let bridgeAttachedForSessionId: string | null = null
let openEpoch = 0
let unmounted = false

const effectiveGeneration = computed(() => props.currentGenerationId || session.value?.generationId || '')
const generationStale = computed(() => Boolean(session.value && effectiveGeneration.value && effectiveGeneration.value !== session.value.generationId))

function teardownChannel() {
  controller?.dispose()
  controller = null
  if (port1) {
    port1.onmessage = null
    port1.close()
    port1 = null
  }
}

async function closeServerSession(target?: ZbaPluginUiSession | null, client?: ZbaClient | null) {
  const current = target ?? session.value
  const owner = client ?? sessionClient ?? props.client
  if (!current) return
  if (current === session.value) {
    session.value = null
    sessionClient = null
    bridgeAttachedForSessionId = null
  }
  try { await owner.closePluginUiSession(current.pluginId, current.sessionId) } catch { /* best-effort lease release */ }
}

async function openSession() {
  const epoch = ++openEpoch
  const client = props.client
  const pluginId = props.pluginId
  teardownChannel()
  const prior = session.value
  const priorClient = sessionClient
  if (prior) {
    void closeServerSession(prior, priorClient)
  }
  if (!props.active) {
    loading.value = false
    return
  }
  loading.value = true
  error.value = null
  try {
    const opened = await client.openPluginUiSession(pluginId)
    if (unmounted || epoch !== openEpoch || client !== props.client || pluginId !== props.pluginId || !props.active) {
      void closeServerSession(opened, client)
      return
    }
    if (!isSafePluginUiEntryUrl(opened.entryUrl, pluginId, opened.releaseSha256)) {
      void closeServerSession(opened, client)
      error.value = { code: 'plugin_ui_invalid_entry_url', message: '服务端返回的插件界面入口地址无效，已拒绝加载。' }
      return
    }
    session.value = opened
    sessionClient = client
    bridgeAttachedForSessionId = null
  } catch (cause) {
    if (unmounted || epoch !== openEpoch || client !== props.client) return
    const uiError = toUiError(cause)
    if (cause instanceof ZbaRequestError && cause.status === 401) {
      emit('auth-expired', uiError)
    }
    error.value = uiError
  } finally {
    if (!unmounted && epoch === openEpoch && client === props.client) {
      loading.value = false
    }
  }
}

function attachBridge() {
  const current = session.value
  const frame = iframeEl.value
  if (!current || !frame?.contentWindow) return
  if (bridgeAttachedForSessionId === current.sessionId) {
    teardownChannel()
    try { frame.src = 'about:blank' } catch { /* ignore */ }
    void closeServerSession(current, sessionClient)
    error.value = {
      code: 'plugin_ui_unexpected_navigation',
      message: '插件界面发生了非预期跳转，已断开受控桥接。请关闭或重试以建立新会话。',
    }
    return
  }
  bridgeAttachedForSessionId = current.sessionId
  teardownChannel()
  const channel = new MessageChannel()
  const localPort = channel.port1
  const localController = createIframeBridgeController(current, () => ({
    client: sessionClient ?? props.client,
    mode: props.mode,
    writesEnabled: props.writesEnabled,
    active: props.active,
    theme: effectiveTheme.value,
    currentGenerationId: effectiveGeneration.value,
    onClose: () => emit('close'),
    onSaved: () => emit('saved'),
    onAuthExpired: (status, code) => emit('auth-expired', { code, message: `认证失效 (${status})`, status }),
  }))
  port1 = localPort
  controller = localController
  localPort.onmessage = async (event: MessageEvent) => {
    const req = event.data
    if (!req || req.type !== 'zba.ui.request' || localPort !== port1 || localController !== controller) return
    const result = await localController.dispatch(req)
    if (localPort === port1) {
      localPort.postMessage({ type: 'zba.ui.response', requestId: req.requestId, ...result })
    }
  }
  frame.contentWindow.postMessage(localController.initPayload(), '*', [channel.port2])
}

watch([() => props.mode, () => props.writesEnabled, effectiveTheme, effectiveGeneration], () => {
  if (!port1 || !controller) return
  port1.postMessage({ ...controller.initPayload(), type: 'zba.ui.state' })
})

watch([() => props.pluginId, () => props.client, () => props.active], () => {
  void openSession()
})

onMounted(() => {
  void openSession()
})

onBeforeUnmount(() => {
  unmounted = true
  ++openEpoch
  teardownChannel()
  void closeServerSession()
})

defineExpose({
  requestLeave: () => Promise.resolve(true),
  draftCount: computed(() => 0),
})
</script>

<template>
  <ManagementDialog
    :title="session?.title || pluginId"
    title-id="plugin-ui-dialog-title"
    test-id="plugin-ui-dialog"
    :active="active"
    :trigger="trigger"
    :fallback="fallback"
    :wide="true"
    @close="emit('close')"
  >
    <div class="plugin-ui-frame-shell">
      <div v-if="error" class="plugin-ui-error-box">
        <ErrorNotice :error="error" />
        <button type="button" class="tonal" data-testid="plugin-ui-retry" @click="openSession">重新建立界面会话</button>
      </div>
      <p v-else-if="loading" class="plugin-ui-loading">正在建立受控插件界面会话…</p>
      <template v-else-if="session">
        <div v-if="generationStale" class="banner inline-notice bad" role="alert">
          插件已切换到新代次，当前旧界面已禁止写入；仍可按原操作键核对未决回执。
        </div>
        <div v-else-if="!session.backendReady || !session.versionMatched" class="banner inline-notice warn" role="alert">
          {{ !session.versionMatched ? '前后端插件版本不匹配，已禁用写入。' : '后端插件未就绪，已禁用写入。' }}
        </div>
        <iframe
          ref="iframeEl"
          class="plugin-ui-iframe"
          sandbox="allow-scripts"
          referrerpolicy="no-referrer"
          :title="session.title"
          :src="session.entryUrl"
          @load="attachBridge"
        />
      </template>
    </div>
  </ManagementDialog>
</template>

<style scoped>
.plugin-ui-frame-shell {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 380px;
}
.plugin-ui-error-box {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 10px;
}
.plugin-ui-iframe {
  width: 100%;
  min-height: 460px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface);
}
.plugin-ui-loading {
  margin: 0;
  color: var(--text-soft);
  font-size: var(--text-sm);
}
</style>
