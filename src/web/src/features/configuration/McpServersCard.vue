<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  ZbaClient,
  ZbaRequestError,
  type ZbaMcpProbeToolView,
  type ZbaMcpServersResponse,
  type ZbaMcpServerTestResult,
  type ZbaMcpServerView
} from '../../api/zba'
import { errorName } from '../../shared/friendly'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import StatusBadge, { type BadgeTone } from '../../shared/ui/StatusBadge.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active?: boolean
}>(), { active: true })

const emit = defineEmits<{
  'enable-admin': []
  'auth-expired': [error: UiError]
}>()

const loading = ref(false)
const busy = ref(false)
const error = ref<UiError | null>(null)
const notice = ref<string | null>(null)
const snapshot = ref<ZbaMcpServersResponse | null>(null)

const formOpen = ref(false)
const editingExisting = ref(false)
const editingHasToken = ref(false)
const formId = ref('')
const formEnabled = ref(true)
const formEndpoint = ref('')
const formAllowedTools = ref('')
const formReadOnlyTools = ref('')
const formTimeout = ref(15)
const formMaxBytes = ref(16384)
const formAuthToken = ref('')
const formClearToken = ref(false)
const probeResult = ref<ZbaMcpServerTestResult | null>(null)

let seq = 0
let unmounted = false

const writable = computed(() => props.mode === 'admin' && props.writesEnabled)

function nextKey(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`
}

function splitToolNames(raw: string): string[] {
  const seen = new Set<string>()
  const result: string[] = []
  for (const part of raw.split(/[\s,;]+/)) {
    const trimmed = part.trim()
    if (!trimmed || seen.has(trimmed)) continue
    seen.add(trimmed)
    result.push(trimmed)
  }
  return result
}

const allowedToolSet = computed(() => new Set(splitToolNames(formAllowedTools.value)))
const readOnlyToolSet = computed(() => new Set(splitToolNames(formReadOnlyTools.value)))

function serverBadge(server: ZbaMcpServerView): { label: string; tone: BadgeTone } {
  if (server.source === 'env' || server.readOnlyLocked) return { label: '环境变量只读', tone: 'neutral' }
  return server.enabled ? { label: '已启用', tone: 'ok' } : { label: '已停用', tone: 'warn' }
}

function handleFailure(err: unknown): void {
  if (err instanceof ZbaRequestError && (err.status === 401 || (err.status === 403 && writable.value))) {
    emit('auth-expired', toUiError(err))
    return
  }
  const uiErr = toUiError(err)
  const friendly = err instanceof ZbaRequestError ? errorName(err.code) : null
  error.value = friendly ? { ...uiErr, message: friendly } : uiErr
}

async function load(): Promise<void> {
  const currentSeq = ++seq
  const client = props.client
  loading.value = true
  error.value = null
  try {
    const data = await client.listMcpServers()
    if (unmounted || currentSeq !== seq || client !== props.client) return
    snapshot.value = data
  } catch (err) {
    if (unmounted || currentSeq !== seq || client !== props.client) return
    handleFailure(err)
  } finally {
    if (!unmounted && currentSeq === seq) loading.value = false
  }
}

function openCreate(): void {
  editingExisting.value = false
  editingHasToken.value = false
  formId.value = ''
  formEnabled.value = true
  formEndpoint.value = ''
  formAllowedTools.value = ''
  formReadOnlyTools.value = ''
  formTimeout.value = 15
  formMaxBytes.value = 16384
  formAuthToken.value = ''
  formClearToken.value = false
  probeResult.value = null
  error.value = null
  notice.value = null
  formOpen.value = true
}

function openEdit(server: ZbaMcpServerView): void {
  if (server.readOnlyLocked) return
  editingExisting.value = true
  editingHasToken.value = server.hasAuthToken
  formId.value = server.id
  formEnabled.value = server.enabled
  formEndpoint.value = server.endpoint
  formAllowedTools.value = server.allowedTools.join(', ')
  formReadOnlyTools.value = server.readOnlyTools.join(', ')
  formTimeout.value = server.callTimeoutSeconds
  formMaxBytes.value = server.maxResultBytes
  formAuthToken.value = ''
  formClearToken.value = false
  probeResult.value = null
  error.value = null
  notice.value = null
  formOpen.value = true
}

function cancelForm(): void {
  formOpen.value = false
  formAuthToken.value = ''
  formClearToken.value = false
  probeResult.value = null
}

function discardSecretsOnLeave(): void {
  formAuthToken.value = ''
  formClearToken.value = false
}

function endSession(): void {
  discardSecretsOnLeave()
  snapshot.value = null
  formOpen.value = false
  probeResult.value = null
  error.value = null
  notice.value = null
}

function hasUnsavedChanges(): boolean {
  return formOpen.value
}

function toggleAllowedTool(name: string): void {
  const list = splitToolNames(formAllowedTools.value)
  const idx = list.indexOf(name)
  if (idx >= 0) {
    list.splice(idx, 1)
    const ro = splitToolNames(formReadOnlyTools.value).filter((item) => item !== name)
    formReadOnlyTools.value = ro.join(', ')
  } else {
    list.push(name)
  }
  formAllowedTools.value = list.join(', ')
}

function toggleReadOnlyTool(name: string): void {
  const allowed = splitToolNames(formAllowedTools.value)
  if (!allowed.includes(name)) allowed.push(name)
  formAllowedTools.value = allowed.join(', ')

  const ro = splitToolNames(formReadOnlyTools.value)
  const idx = ro.indexOf(name)
  if (idx >= 0) ro.splice(idx, 1)
  else ro.push(name)
  formReadOnlyTools.value = ro.join(', ')
}

function probeCandidateLabel(tool: ZbaMcpProbeToolView): string {
  return tool.readOnlyHint ? `${tool.name} (readOnlyHint)` : tool.name
}

async function testConnection(): Promise<void> {
  if (!writable.value || busy.value) return
  busy.value = true
  error.value = null
  notice.value = null
  try {
    const res = await props.client.testMcpServer({
      endpoint: formEndpoint.value.trim(),
      id: editingExisting.value ? formId.value.trim() : null,
      authToken: formAuthToken.value.trim() || null,
      callTimeoutSeconds: Number(formTimeout.value) || 10
    })
    probeResult.value = res
    notice.value = res.ok
      ? `探测成功：发现 ${res.tools.length} 个远端工具（勾选加入 allowedTools 后才会纳入目录）`
      : `探测未通过：${errorName(res.reasonCode) ?? res.reasonCode}`
  } catch (err) {
    handleFailure(err)
  } finally {
    busy.value = false
  }
}

async function saveServer(): Promise<void> {
  if (!writable.value || busy.value) return
  if (!snapshot.value) await load()
  if (!snapshot.value) return

  const allowedTools = splitToolNames(formAllowedTools.value)
  const allowedSet = new Set(allowedTools)
  const readOnlyTools = splitToolNames(formReadOnlyTools.value).filter((name) => allowedSet.has(name))

  busy.value = true
  error.value = null
  notice.value = null
  try {
    const res = await props.client.saveMcpServer({
      expectedRevision: snapshot.value.revision,
      idempotencyKey: nextKey('mcp-save'),
      id: formId.value.trim(),
      enabled: formEnabled.value,
      endpoint: formEndpoint.value.trim(),
      allowedTools,
      readOnlyTools,
      callTimeoutSeconds: Number(formTimeout.value) || 15,
      maxResultBytes: Number(formMaxBytes.value) || 16384,
      authToken: formAuthToken.value.trim() || null,
      clearAuthToken: formClearToken.value
    })
    snapshot.value = res.snapshot
    formAuthToken.value = ''
    formClearToken.value = false
    formOpen.value = false
    notice.value = 'MCP 服务端已保存并完成运行态热重载。'
  } catch (err) {
    handleFailure(err)
  } finally {
    busy.value = false
  }
}

async function deleteServer(id: string): Promise<void> {
  if (!writable.value || busy.value || !snapshot.value) return
  busy.value = true
  error.value = null
  notice.value = null
  try {
    const res = await props.client.deleteMcpServer({
      expectedRevision: snapshot.value.revision,
      idempotencyKey: nextKey('mcp-del'),
      id
    })
    snapshot.value = res.snapshot
    if (formOpen.value && formId.value === id) cancelForm()
    notice.value = `已删除控制台 MCP 服务端「${id}」并刷新工具目录。`
  } catch (err) {
    handleFailure(err)
  } finally {
    busy.value = false
  }
}

async function reloadAll(): Promise<void> {
  if (!writable.value || busy.value) return
  busy.value = true
  error.value = null
  notice.value = null
  try {
    const res = await props.client.reloadMcpServers()
    snapshot.value = res.snapshot
    notice.value = `已完成 MCP 工具热重载，当前共发现 ${res.toolCount} 个可用工具。`
  } catch (err) {
    handleFailure(err)
  } finally {
    busy.value = false
  }
}

function onClearTokenChange(): void {
  if (formClearToken.value) formAuthToken.value = ''
}

function onAuthTokenInput(): void {
  if (formAuthToken.value.length > 0 && formClearToken.value)
    formClearToken.value = false
}

watch(writable, (nextWritable) => {
  if (!nextWritable) {
    cancelForm()
  }
})

watch(() => [props.client, props.active] as const, ([, isActive]) => {
  if (isActive) void load()
})

onMounted(() => {
  if (props.active) void load()
})

onBeforeUnmount(() => {
  unmounted = true
  discardSecretsOnLeave()
})

defineExpose({ reload: load, discardSecretsOnLeave, cancelForm, endSession, hasUnsavedChanges })
</script>

<template>
  <section class="panel mcp-card" data-testid="mcp-servers-card" aria-labelledby="mcp-servers-title">
    <div class="panel-heading">
      <div>
        <h3 id="mcp-servers-title">MCP 远端工具服务端（P4.3 热重载）</h3>
        <p class="mcp-sub">
          环境变量配置保持只读优先；控制台新增服务端需显式配置允许工具清单并经过 SSRF 安全校验，凭据仅写入不回显。
        </p>
      </div>
      <div class="mcp-actions">
        <StatusBadge
          v-if="snapshot"
          tone="accent"
          :label="`已发现 ${snapshot.discoveredTools.length} 个工具 · v${snapshot.revision}`"
        />
        <button type="button" class="tonal small" data-testid="mcp-refresh-btn" :disabled="loading || busy" @click="load">
          刷新
        </button>
        <button
          v-if="writable"
          type="button"
          class="tonal small"
          data-testid="mcp-reload-btn"
          :disabled="loading || busy"
          @click="reloadAll"
        >
          热重载工具目录
        </button>
        <button
          v-if="writable"
          type="button"
          class="primary small"
          data-testid="mcp-new-btn"
          :disabled="loading || busy"
          @click="openCreate"
        >
          新增服务端
        </button>
        <button
          v-else-if="props.mode !== 'admin'"
          type="button"
          class="tonal small"
          data-testid="mcp-enable-admin"
          @click="emit('enable-admin')"
        >
          启用管理员模式
        </button>
      </div>
    </div>

    <ErrorNotice :error="error" />
    <p v-if="notice" class="mcp-notice" data-testid="mcp-notice">{{ notice }}</p>

    <div v-if="snapshot && snapshot.servers.length > 0" class="mcp-list" data-testid="mcp-server-list">
      <article
        v-for="server in snapshot.servers"
        :key="server.id"
        class="mcp-row"
        :data-testid="`mcp-server-row-${server.id}`"
      >
        <div class="mcp-row-head">
          <div class="mcp-row-title">
            <strong>{{ server.id }}</strong>
            <StatusBadge :tone="serverBadge(server).tone" :label="serverBadge(server).label" />
            <StatusBadge v-if="server.hasAuthToken" tone="ok" label="已配置凭据" />
          </div>
          <div v-if="writable && !server.readOnlyLocked" class="mcp-row-actions">
            <button
              type="button"
              class="tonal small"
              :data-testid="`mcp-edit-${server.id}`"
              :disabled="busy"
              @click="openEdit(server)"
            >
              编辑
            </button>
            <button
              type="button"
              class="danger small"
              :data-testid="`mcp-delete-${server.id}`"
              :disabled="busy"
              @click="deleteServer(server.id)"
            >
              删除
            </button>
          </div>
        </div>
        <p class="mcp-endpoint">{{ server.endpoint }}</p>
        <p class="mcp-meta">
          <span>允许工具：{{ server.allowedTools.length ? server.allowedTools.join(', ') : '（无）' }}</span>
          <span v-if="server.readOnlyTools.length"> · 聊天只读白名单：{{ server.readOnlyTools.join(', ') }}</span>
          <span> · 超时 {{ server.callTimeoutSeconds }}s / 上限 {{ server.maxResultBytes }}B</span>
        </p>
      </article>
    </div>

    <p v-else class="mcp-empty" data-testid="mcp-server-empty">
      {{ loading ? '正在加载 MCP 服务端配置…' : '暂未配置托管的 MCP 远端服务端。' }}
    </p>

    <div v-if="snapshot && snapshot.discoveredTools.length > 0" class="mcp-discovered" data-testid="mcp-discovered-tools">
      <h4>当前已激活工具</h4>
      <div class="mcp-chip-grid">
        <span v-for="tool in snapshot.discoveredTools" :key="tool.qualifiedName" class="mcp-tool-chip">
          <code>{{ tool.qualifiedName }}</code>
          <StatusBadge
            :tone="tool.readOnlyConfirmed ? 'ok' : 'warn'"
            :label="tool.readOnlyConfirmed ? '已确认只读' : '需审批'"
          />
        </span>
      </div>
    </div>

    <form v-if="formOpen && writable" class="mcp-form" data-testid="mcp-editor-form" @submit.prevent="saveServer">
      <h4>{{ editingExisting ? `编辑 MCP 服务端：${formId}` : '新增 MCP 服务端' }}</h4>
      <div class="mcp-grid">
        <label class="mcp-field">
          <span>服务端 ID</span>
          <input
            v-model="formId"
            type="text"
            data-testid="mcp-input-id"
            :disabled="editingExisting"
            placeholder="如 docs-mcp（字母/数字/连字符）"
          />
        </label>
        <label class="mcp-field">
          <span>Streamable HTTP Endpoint</span>
          <input
            v-model="formEndpoint"
            type="url"
            data-testid="mcp-input-endpoint"
            placeholder="https://mcp.example.com/mcp"
          />
        </label>
        <label class="mcp-field mcp-check">
          <input v-model="formEnabled" type="checkbox" data-testid="mcp-input-enabled" />
          <span>启用该服务端</span>
        </label>
      </div>

      <div class="mcp-grid">
        <label class="mcp-field">
          <span>允许工具清单（allowedTools，逗号或空格分隔）</span>
          <input
            v-model="formAllowedTools"
            type="text"
            data-testid="mcp-input-allowed-tools"
            placeholder="search_docs, create_ticket"
          />
        </label>
        <label class="mcp-field">
          <span>聊天只读白名单（readOnlyTools，须属于 allowedTools）</span>
          <input
            v-model="formReadOnlyTools"
            type="text"
            data-testid="mcp-input-readonly-tools"
            placeholder="search_docs"
          />
        </label>
      </div>

      <div class="mcp-grid">
        <label class="mcp-field">
          <span>调用超时（秒）</span>
          <input v-model.number="formTimeout" type="number" min="1" max="120" data-testid="mcp-input-timeout" />
        </label>
        <label class="mcp-field">
          <span>单次返回字节上限</span>
          <input v-model.number="formMaxBytes" type="number" min="1024" max="262144" data-testid="mcp-input-max-bytes" />
        </label>
        <label class="mcp-field">
          <span>Bearer Token（仅写入、不回显）</span>
          <input
            v-model="formAuthToken"
            type="password"
            autocomplete="new-password"
            data-testid="mcp-input-token"
            :disabled="formClearToken"
            :placeholder="editingExisting && editingHasToken ? '留空保持现有凭据不变' : '可选，例如 sk-...'"
            @input="onAuthTokenInput"
          />
        </label>
      </div>

      <label v-if="editingExisting && editingHasToken" class="mcp-field mcp-check">
        <input v-model="formClearToken" type="checkbox" data-testid="mcp-input-clear-token" @change="onClearTokenChange" />
        <span>清除已存储的 Bearer Token</span>
      </label>

      <div v-if="probeResult" class="mcp-probe" data-testid="mcp-probe-result">
        <p class="mcp-probe-title">
          探测结果：<strong>{{ probeResult.ok ? '连接成功' : probeResult.reasonCode }}</strong>
        </p>
        <div v-if="probeResult.tools.length" class="mcp-probe-list">
          <div v-for="cand in probeResult.tools" :key="cand.name" class="mcp-probe-item">
            <button
              type="button"
              class="tonal small"
              :data-testid="`mcp-probe-toggle-${cand.name}`"
              @click="toggleAllowedTool(cand.name)"
            >
              {{ allowedToolSet.has(cand.name) ? '✓ 已允许：' : '+ 允许：' }}{{ probeCandidateLabel(cand) }}
            </button>
            <button
              type="button"
              class="tonal small"
              :data-testid="`mcp-probe-ro-${cand.name}`"
              @click="toggleReadOnlyTool(cand.name)"
            >
              {{ readOnlyToolSet.has(cand.name) ? '✓ 聊天只读' : '标记只读' }}
            </button>
          </div>
        </div>
      </div>

      <div class="mcp-form-actions">
        <button type="button" class="tonal small" data-testid="mcp-test-btn" :disabled="busy" @click="testConnection">
          测试连接并发现工具
        </button>
        <button type="submit" class="primary small" data-testid="mcp-save-btn" :disabled="busy">
          保存并热生效
        </button>
        <button type="button" class="tonal small" data-testid="mcp-cancel-btn" :disabled="busy" @click="cancelForm">
          取消
        </button>
      </div>
    </form>
  </section>
</template>

<style scoped>
.mcp-card { margin-top: var(--space-4, 1rem); }
.mcp-sub { margin: 0.25rem 0 0; color: var(--text-muted, #64748b); font-size: 0.875rem; }
.mcp-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; }
.mcp-notice { margin: 0.5rem 0; color: var(--ok, #15803d); font-size: 0.875rem; }
.mcp-list { display: flex; flex-direction: column; gap: 0.75rem; margin-top: 0.75rem; }
.mcp-row { padding: 0.75rem; border: 1px solid var(--border, #e2e8f0); border-radius: var(--radius, 8px); }
.mcp-row-head { display: flex; justify-content: space-between; align-items: center; gap: 0.75rem; flex-wrap: wrap; }
.mcp-row-title { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
.mcp-row-actions { display: flex; flex-wrap: wrap; gap: 0.5rem; }
.mcp-endpoint { margin: 0.35rem 0 0.25rem; font-family: monospace; font-size: 0.8125rem; word-break: break-all; }
.mcp-meta { margin: 0; color: var(--text-muted, #64748b); font-size: 0.8125rem; }
.mcp-empty { color: var(--text-muted, #64748b); font-size: 0.875rem; margin-top: 0.75rem; }
.mcp-discovered { margin-top: 0.75rem; padding-top: 0.75rem; border-top: 1px solid var(--border, #e2e8f0); }
.mcp-discovered h4 { margin: 0 0 0.5rem; font-size: 0.875rem; }
.mcp-chip-grid { display: flex; flex-wrap: wrap; gap: 0.5rem; }
.mcp-tool-chip { display: inline-flex; align-items: center; gap: 0.35rem; padding: 0.25rem 0.5rem; border: 1px solid var(--border, #e2e8f0); border-radius: 6px; font-size: 0.8125rem; overflow-wrap: anywhere; max-width: 100%; }
.mcp-form { margin-top: 1rem; padding: 0.875rem; border: 1px solid var(--border, #cbd5e1); border-radius: var(--radius, 8px); }
.mcp-form h4 { margin: 0 0 0.75rem; font-size: 0.9375rem; }
.mcp-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(220px, 100%), 1fr)); gap: 0.75rem; margin-bottom: 0.75rem; }
.mcp-field { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.8125rem; }
.mcp-field input[type="text"],
.mcp-field input[type="url"],
.mcp-field input[type="number"],
.mcp-field input[type="password"] { padding: 0.45rem 0.6rem; border: 1px solid var(--border, #cbd5e1); border-radius: 6px; }
.mcp-check { flex-direction: row; align-items: center; gap: 0.5rem; padding-top: 0.75rem; }
.mcp-probe { margin: 0.5rem 0 0.75rem; padding: 0.625rem; border: 1px dashed var(--border, #cbd5e1); border-radius: 6px; }
.mcp-probe-title { margin: 0 0 0.5rem; font-size: 0.8125rem; }
.mcp-probe-list { display: flex; flex-direction: column; gap: 0.4rem; }
.mcp-probe-item { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: center; }
.mcp-form-actions { display: flex; flex-wrap: wrap; gap: 0.5rem; margin-top: 0.75rem; }
</style>
