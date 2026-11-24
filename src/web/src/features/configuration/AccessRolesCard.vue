<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  ZbaClient,
  ZbaRequestError,
  type WorkspaceRoleValue,
  type ZbaAccessRoleBindingInput,
  type ZbaAccessRolesResponse
} from '../../api/zba'
import { errorName, workspaceRoleLabel } from '../../shared/friendly'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import StatusBadge from '../../shared/ui/StatusBadge.vue'
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

interface DraftRoleRow {
  platformId: string
  accountScope: string
  instanceId: string
  scopeConversationKey: string
  nativeUserId: string
  role: WorkspaceRoleValue
  note: string
}

const ROLE_OPTIONS: readonly WorkspaceRoleValue[] = [0, 1, 2, 3, 4]
const PLATFORM_OPTIONS = ['qq.private', 'qq.official', 'feishu', 'local'] as const

const loading = ref(false)
const busy = ref(false)
const error = ref<UiError | null>(null)
const notice = ref<string | null>(null)
const snapshot = ref<ZbaAccessRolesResponse | null>(null)
const drafts = ref<DraftRoleRow[]>([])
const dirty = ref(false)

let seq = 0
let unmounted = false

const writable = computed(() => props.mode === 'admin' && props.writesEnabled)

function syncDraftsFromSnapshot(data: ZbaAccessRolesResponse): void {
  drafts.value = data.items.map((item) => ({
    platformId: item.platformId,
    accountScope: item.accountScope,
    instanceId: item.instanceId,
    scopeConversationKey: item.scopeConversationKeyRaw ?? '',
    nativeUserId: item.nativeUserIdRaw ?? '',
    role: item.role,
    note: item.noteRaw ?? item.note ?? ''
  }))
  dirty.value = false
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

async function load(preserveDirty = false): Promise<void> {
  const currentSeq = ++seq
  const client = props.client
  loading.value = true
  error.value = null
  try {
    const data = await client.listAccessRoles()
    if (unmounted || currentSeq !== seq || client !== props.client) return
    snapshot.value = data
    if (!preserveDirty || !dirty.value)
      syncDraftsFromSnapshot(data)
  } catch (err) {
    if (unmounted || currentSeq !== seq || client !== props.client) return
    handleFailure(err)
  } finally {
    if (!unmounted && currentSeq === seq) loading.value = false
  }
}

function defaultScopesForPlatform(platformId: string): { accountScope: string; instanceId: string } {
  switch (platformId) {
    case 'feishu':
      return { accountScope: 'default', instanceId: 'feishu-main' }
    case 'qq.private':
    case 'qq.official':
    case 'local':
    default:
      return { accountScope: 'legacy', instanceId: '' }
  }
}

function addBinding(): void {
  const defaults = defaultScopesForPlatform('feishu')
  drafts.value.push({
    platformId: 'feishu',
    accountScope: defaults.accountScope,
    instanceId: defaults.instanceId,
    scopeConversationKey: '',
    nativeUserId: '',
    role: 3,
    note: ''
  })
  dirty.value = true
  notice.value = null
  error.value = null
}

function onPlatformChange(row: DraftRoleRow): void {
  const defaults = defaultScopesForPlatform(row.platformId)
  row.accountScope = defaults.accountScope
  row.instanceId = defaults.instanceId
  markDirty()
}

function removeBinding(index: number): void {
  drafts.value.splice(index, 1)
  dirty.value = true
  notice.value = null
}

function markDirty(): void {
  dirty.value = true
  notice.value = null
}

async function saveRoles(): Promise<void> {
  if (!writable.value || busy.value) return
  if (!snapshot.value) await load()
  if (!snapshot.value) return

  busy.value = true
  error.value = null
  notice.value = null
  try {
    const bindings: ZbaAccessRoleBindingInput[] = drafts.value.map((row) => {
      const platformId = row.platformId.trim()
      return {
        platformId,
        accountScope: row.accountScope.trim() || defaultScopesForPlatform(platformId).accountScope,
        instanceId: row.instanceId.trim(),
        scopeConversationKey: row.scopeConversationKey.trim() || null,
        nativeUserId: row.nativeUserId.trim(),
        role: row.role,
        note: row.note.trim() || null
      }
    })
    const res = await props.client.saveAccessRoles({
      expectedRevision: snapshot.value.revision,
      idempotencyKey: `rbac-save-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`,
      bindings
    })
    snapshot.value = res.snapshot
    syncDraftsFromSnapshot(res.snapshot)
    notice.value = `已保存 ${res.snapshot.items.length} 条跨平台角色绑定并热应用到权限解析器。`
  } catch (err) {
    handleFailure(err)
  } finally {
    busy.value = false
  }
}

function endSession(): void {
  snapshot.value = null
  drafts.value = []
  dirty.value = false
  error.value = null
  notice.value = null
}

function hasUnsavedChanges(): boolean {
  return dirty.value
}

function discardDraftsOnLeave(): void {
  drafts.value = []
  dirty.value = false
}

watch(writable, (nextWritable) => {
  if (!nextWritable) {
    discardDraftsOnLeave()
    if (props.active) void load(false)
  }
})

watch(() => [props.client, props.active, props.mode] as const, ([nextClient, isActive], oldVal) => {
  const clientChanged = oldVal && nextClient !== oldVal[0]
  if (clientChanged) dirty.value = false
  if (isActive) void load(!clientChanged)
})

onMounted(() => {
  if (props.active) void load()
})

onBeforeUnmount(() => { unmounted = true })

defineExpose({ reload: () => load(false), endSession, hasUnsavedChanges, discardDraftsOnLeave })
</script>

<template>
  <section class="panel rbac-card" data-testid="access-roles-card" aria-labelledby="access-roles-title">
    <div class="panel-heading">
      <div>
        <h3 id="access-roles-title">跨平台群组与私聊角色权限（RBAC）</h3>
        <p class="rbac-sub">
          展示列遵循脱敏开关；管理员编辑字段直接绑定原始标识（*Raw），绝不会把脱敏占位符写回存储。本群管理员必须绑定具体群聊会话 Key。
        </p>
      </div>
      <div class="rbac-actions">
        <StatusBadge
          v-if="snapshot"
          tone="accent"
          :label="`${snapshot.items.length} 条绑定 · v${snapshot.revision}`"
        />
        <button type="button" class="tonal small" data-testid="rbac-refresh-btn" :disabled="loading || busy" @click="load(false)">
          刷新
        </button>
        <button
          v-if="writable"
          type="button"
          class="tonal small"
          data-testid="rbac-add-btn"
          :disabled="loading || busy"
          @click="addBinding"
        >
          新增绑定
        </button>
        <button
          v-if="writable"
          type="button"
          class="primary small"
          data-testid="rbac-save-btn"
          :disabled="loading || busy"
          @click="saveRoles"
        >
          保存角色绑定
        </button>
        <button
          v-else-if="props.mode !== 'admin'"
          type="button"
          class="tonal small"
          data-testid="rbac-enable-admin"
          @click="emit('enable-admin')"
        >
          启用管理员模式
        </button>
      </div>
    </div>

    <ErrorNotice :error="error" />
    <p v-if="notice" class="rbac-notice" data-testid="rbac-notice">{{ notice }}</p>

    <div v-if="writable">
      <div v-if="drafts.length > 0" class="rbac-rows" data-testid="rbac-draft-list">
        <div v-for="(row, idx) in drafts" :key="idx" class="rbac-row" :data-testid="`rbac-row-${idx}`">
          <div class="rbac-grid">
            <label class="rbac-field">
              <span>平台</span>
              <select v-model="row.platformId" :data-testid="`rbac-platform-${idx}`" @change="onPlatformChange(row)">
                <option v-for="plat in PLATFORM_OPTIONS" :key="plat" :value="plat">{{ plat }}</option>
              </select>
            </label>
            <label class="rbac-field">
              <span>AccountScope</span>
              <input v-model="row.accountScope" type="text" :data-testid="`rbac-account-${idx}`" @input="markDirty" />
            </label>
            <label class="rbac-field">
              <span>InstanceId</span>
              <input v-model="row.instanceId" type="text" :data-testid="`rbac-instance-${idx}`" @input="markDirty" />
            </label>
            <label class="rbac-field">
              <span>用户原始 ID</span>
              <input
                v-model="row.nativeUserId"
                type="text"
                :data-testid="`rbac-user-${idx}`"
                placeholder="如 ou_synthetic_10001 或 10001"
                @input="markDirty"
              />
            </label>
            <label class="rbac-field">
              <span>角色</span>
              <select v-model.number="row.role" :data-testid="`rbac-role-${idx}`" @change="markDirty">
                <option v-for="r in ROLE_OPTIONS" :key="r" :value="r">{{ workspaceRoleLabel(r) }}</option>
              </select>
            </label>
            <label class="rbac-field">
              <span>群会话范围（GroupManager 必填）</span>
              <input
                v-model="row.scopeConversationKey"
                type="text"
                :data-testid="`rbac-scope-${idx}`"
                placeholder="全局留空；如 feishu:group:oc_..."
                @input="markDirty"
              />
            </label>
            <label class="rbac-field">
              <span>备注</span>
              <input v-model="row.note" type="text" :data-testid="`rbac-note-${idx}`" @input="markDirty" />
            </label>
          </div>
          <div class="rbac-row-foot">
            <span
              v-if="row.role === 3 && !row.scopeConversationKey.trim()"
              class="rbac-warn"
              :data-testid="`rbac-scope-warning-${idx}`"
            >
              本群管理员（GroupManager）必须指定单个群聊会话 Key，禁止空作用域越权。
            </span>
            <button
              type="button"
              class="danger small"
              :data-testid="`rbac-remove-${idx}`"
              :disabled="busy"
              @click="removeBinding(idx)"
            >
              移除
            </button>
          </div>
        </div>
      </div>
      <p v-else class="rbac-empty" data-testid="rbac-empty">
        暂无角色绑定；可点击“新增绑定”添加，或保存空列表以清空控制台覆盖。
      </p>
    </div>

    <div v-else>
      <div v-if="snapshot && snapshot.items.length > 0" class="rbac-readonly" data-testid="rbac-readonly-list">
        <div v-for="(item, idx) in snapshot.items" :key="idx" class="rbac-readonly-row">
          <strong>{{ item.platformId }}</strong>
          <span>{{ item.nativeUserId }}</span>
          <StatusBadge tone="neutral" :label="workspaceRoleLabel(item.role)" />
          <span>{{ item.scopeConversationKey ?? '全局' }}</span>
        </div>
      </div>
      <p v-else class="rbac-empty" data-testid="rbac-empty">
        {{ loading ? '正在读取角色权限绑定…' : '暂无跨平台角色权限绑定。' }}
      </p>
    </div>
  </section>
</template>

<style scoped>
.rbac-card { margin-top: var(--space-4, 1rem); }
.rbac-sub { margin: 0.25rem 0 0; color: var(--text-muted, #64748b); font-size: 0.875rem; }
.rbac-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; }
.rbac-notice { margin: 0.5rem 0; color: var(--ok, #15803d); font-size: 0.875rem; }
.rbac-rows { display: flex; flex-direction: column; gap: 0.75rem; margin-top: 0.75rem; }
.rbac-row { padding: 0.75rem; border: 1px solid var(--border, #e2e8f0); border-radius: var(--radius, 8px); }
.rbac-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(180px, 100%), 1fr)); gap: 0.625rem; }
.rbac-field { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.8125rem; }
.rbac-field input,
.rbac-field select { padding: 0.4rem 0.55rem; border: 1px solid var(--border, #cbd5e1); border-radius: 6px; }
.rbac-row-foot { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 0.5rem; margin-top: 0.5rem; }
.rbac-warn { color: var(--warn, #b45309); font-size: 0.8125rem; }
.rbac-readonly { display: flex; flex-direction: column; gap: 0.5rem; margin-top: 0.75rem; }
.rbac-readonly-row { display: flex; flex-wrap: wrap; align-items: center; gap: 0.75rem; padding: 0.5rem 0.75rem; border: 1px solid var(--border, #e2e8f0); border-radius: 6px; font-size: 0.875rem; overflow-wrap: anywhere; }
.rbac-empty { color: var(--text-muted, #64748b); font-size: 0.875rem; margin-top: 0.75rem; }
</style>
