<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { filterPluginCatalog, pluginPackageStatus, pluginReviewLabel, pluginReviewed, pluginRuntime, pluginSourceLabel, pluginVersion, safeGithubLink, type PackageCatalogEntry, type PluginCatalogEntry } from './plugin-catalog'
const props = defineProps<{
  entries: PluginCatalogEntry[]
  canWrite: boolean
  busy?: boolean
  loading?: boolean
  unresolvedIds?: string[]
  focusPluginId?: string
}>()
const emit = defineEmits<{
  install: [entry: PackageCatalogEntry]
  uninstall: [entry: PackageCatalogEntry]
  settings: [entry: PluginCatalogEntry]
}>()
const search = ref('')
const category = ref('all')
const source = ref('all')
const runtime = ref('all')
const review = ref('all')
const state = ref('all')
const selectedId = ref<string | null>(props.focusPluginId ?? null)
watch(() => props.focusPluginId, id => { if (id) selectedId.value = id })
const trusted = ref(false)
const uninstallArmed = ref(false)
const categories = computed(() => [...new Set(props.entries.map(entry => entry.category))].sort())
const entries = computed(() => filterPluginCatalog(props.entries, search.value, category.value, { source: source.value, runtime: runtime.value, review: review.value, state: state.value }))
const selected = computed(() => entries.value.find(entry => entry.id === selectedId.value) ?? entries.value[0])
const repository = computed(() => safeGithubLink(selected.value?.repository))
const pending = computed(() => Boolean(selected.value?.pendingVersion || selected.value?.pendingUninstall || selected.value?.restartRequired))
const unresolved = computed(() => Boolean(selected.value && props.unresolvedIds?.includes(selected.value.id)))
const sameVersion = computed(() => selected.value?.kind !== 'builtin' && selected.value?.installedVersion === selected.value?.manifest.version)
const unsupported = computed(() => selected.value && pluginRuntime(selected.value) === 'python')
watch(() => [selected.value?.id, selected.value?.manifest?.sha256, selected.value && pluginVersion(selected.value), pending.value, unresolved.value], () => { trusted.value = false; uninstallArmed.value = false })
function install(): void {
  if (!selected.value || selected.value.kind === 'builtin' || !props.canWrite || props.busy || unresolved.value || pending.value || sameVersion.value || unsupported.value || !trusted.value) return
  emit('install', selected.value)
}
function uninstall(): void {
  if (!selected.value || selected.value.kind === 'builtin' || !props.canWrite || props.busy || unresolved.value || pending.value || !selected.value.installedVersion || !uninstallArmed.value) return
  emit('uninstall', selected.value)
  uninstallArmed.value = false
}
</script>

<template>
  <section class="plugin-catalog" data-testid="plugin-catalog">
    <header class="catalog-heading"><div><p class="catalog-eyebrow">能力目录</p><h2>发现适合机器人的能力</h2></div><span class="catalog-count">{{ entries.length }} / {{ props.entries.length }} 项</span></header>
    <p class="catalog-scope">内置能力随宿主提供，只需管理配置；扩展包需单独下载，并由你确认来源与权限风险。</p>
    <div class="catalog-filters">
      <label class="catalog-search">搜索插件<input v-model="search" type="search" placeholder="中文名、功能、标签或插件 ID" data-testid="marketplace-search"></label>
      <label>分类<select v-model="category" data-testid="marketplace-category"><option value="all">全部分类</option><option v-for="item in categories" :key="item" :value="item">{{ item }}</option></select></label>
      <label>来源<select v-model="source" data-testid="catalog-source"><option value="all">全部来源</option><option value="builtin">内置能力</option><option value="package">可下载扩展</option><option value="official">官方发布</option><option value="community">社区来源</option></select></label>
      <label>运行时<select v-model="runtime" data-testid="catalog-runtime"><option value="all">全部运行时</option><option value="builtin">内置</option><option value="dotnet">.NET</option><option value="python">Python（暂不支持运行）</option></select></label>
      <label>审核<select v-model="review" data-testid="catalog-review"><option value="all">全部审核状态</option><option value="reviewed">已审核此版本</option><option value="unreviewed">未单独审核</option></select></label>
      <label>状态<select v-model="state" data-testid="catalog-state"><option value="all">全部状态</option><option value="installed">已内置 / 已安装</option><option value="available">未安装</option><option value="enabled">已启用</option><option value="disabled">已停用</option><option value="pending">待应用</option></select></label>
    </div>
    <p v-if="props.loading" role="status" data-testid="catalog-loading">正在读取目录与宿主状态…</p>
    <div v-else class="catalog-layout">
      <div class="catalog-list" aria-label="插件列表" data-testid="marketplace-list">
        <button v-for="entry in entries" :key="entry.id" type="button" class="catalog-entry" :data-testid="`marketplace-item-${entry.id}`" :class="{ selected: selected?.id === entry.id }" :aria-pressed="selected?.id === entry.id" @click="selectedId = entry.id">
          <span class="entry-kicker">{{ pluginSourceLabel(entry) }} · {{ entry.category }}</span>
          <span class="entry-title">{{ entry.name }}</span><code>{{ entry.id }}</code>
          <span class="entry-summary">{{ entry.summary }}</span>
          <span class="entry-state">{{ pluginPackageStatus(entry) }}</span>
        </button>
        <p v-if="entries.length === 0" class="empty-copy" data-testid="marketplace-empty">没有匹配的能力。试试其他关键词，或放宽筛选条件。</p>
      </div>
      <article v-if="selected" class="catalog-detail" data-testid="marketplace-detail">
        <div class="detail-badges"><span>{{ pluginSourceLabel(selected) }}</span><span>{{ pluginRuntime(selected) === 'dotnet' ? '.NET' : pluginRuntime(selected) === 'builtin' ? '内置' : 'Python' }}</span><span :class="{ reviewed: pluginReviewed(selected) }" data-testid="review-badge">{{ pluginReviewLabel(selected) }}</span></div>
        <h3>{{ selected.name }}</h3><code class="plugin-id">{{ selected.id }}</code><p class="detail-summary">{{ selected.summary }}</p>
        <p class="package-state" data-testid="marketplace-package-state">{{ pluginPackageStatus(selected) }}</p>
        <p v-if="selected.tags?.length" class="plugin-tags"><span v-for="tag in selected.tags" :key="tag">{{ tag }}</span></p>
        <dl class="detail-facts"><dt>版本</dt><dd data-testid="marketplace-version">{{ pluginVersion(selected) }}</dd><dt>作者</dt><dd>{{ selected.author || '未提供' }}</dd><dt>许可证</dt><dd>{{ selected.metadata?.license || '未提供' }}</dd><dt>项目地址</dt><dd><a v-if="repository" :href="repository" target="_blank" rel="noopener noreferrer">查看 GitHub 项目 ↗</a><span v-else>未提供可安全打开的项目地址</span></dd></dl>
        <section class="description-sections">
          <h4>功能介绍</h4><p>{{ selected.metadata?.description || selected.summary }}</p>
          <h4>如何使用</h4><p>{{ selected.metadata?.usage || '请查阅项目文档；目录暂未提供使用说明。' }}</p>
          <h4>配置说明</h4><p>{{ selected.metadata?.configuration || '未提供额外配置说明。' }}</p>
          <h4>兼容要求</h4><p>{{ selected.metadata?.compatibility || (selected.kind === 'builtin' ? '与当前宿主版本一同提供。' : selected.manifest.targetFramework + ' · API v' + selected.manifest.apiVersion) }}</p>
          <h4>维护与更新</h4><p>{{ selected.metadata?.maintenance || '未提供维护说明。' }}</p><p>{{ selected.metadata?.changelog || '未提供更新记录。' }}</p>
        </section>
        <section class="catalog-risk" data-testid="marketplace-trust-warning"><h4>权限与风险</h4><p>{{ selected.metadata?.permissionNotes || '请在启用前核对功能权限与配置。' }}</p><p>{{ selected.metadata?.riskNotice || '来源标签不代表安全承诺，请自行核对代码和权限。' }}</p>
          <template v-if="selected.kind !== 'builtin'"><p>扩展包会在宿主内执行代码，不是沙箱。官方来源、哈希校验或审核记录，都不等于安全隔离。</p><ul data-testid="marketplace-permissions"><li v-for="permission in selected.manifest.permissions ?? []" :key="permission">{{ permission }}</li><li v-if="!selected.manifest.permissions?.length">包未声明权限；不代表没有风险。</li></ul></template>
          <p v-else>这是已注册的宿主内置能力，不下载 ZIP，也不通过包安装、更新或卸载接口管理。</p>
        </section>
        <template v-if="selected.kind !== 'builtin'">
          <details class="artifact-details"><summary>包与审核详情</summary><dl><dt>SHA-256（包一致性）</dt><dd class="hash" data-testid="marketplace-sha256">{{ selected.manifest.sha256 }}</dd><dt>运行目标</dt><dd>{{ selected.manifest.targetFramework }} · API v{{ selected.manifest.apiVersion }}</dd><dt>最低宿主</dt><dd>{{ selected.manifest.minimumHostVersion || '未限制' }}</dd><dt>审核记录</dt><dd>{{ pluginReviewLabel(selected) }}</dd><template v-if="pluginReviewed(selected)"><dt>审核人 / 时间</dt><dd>{{ selected.metadata?.review.reviewer }} · {{ selected.metadata?.review.reviewedAt }}</dd><dt>源码版本</dt><dd class="hash">{{ selected.metadata?.review.sourceRevision }}</dd></template></dl></details>
          <p v-if="unsupported" class="pending-copy" role="status">Python 运行时尚未开放；本阶段只能查看说明，不能安装运行。</p>
          <p v-if="pending" class="pending-copy" role="status">已有变更待应用；{{ selected.restartRequired ? '需要重启宿主' : '等待宿主热应用' }}，完成前不能重复安装或卸载。</p>
          <p v-if="unresolved" class="pending-copy" role="status">上次请求结果尚未确认，请先核对操作状态；现在不能重复提交。</p>
          <label v-if="!sameVersion && !unsupported" class="trust"><input v-model="trusted" type="checkbox" :disabled="!props.canWrite || props.busy || pending || unresolved" data-testid="marketplace-trust">我信任此来源，并接受在宿主内执行代码的权限风险</label>
          <p class="read-only">操作结果以回执为准：等待热应用、已生效或需重启宿主会分别提示。</p><div class="catalog-actions"><button type="button" :disabled="!props.canWrite || props.busy || unresolved || pending || sameVersion || unsupported || !trusted" data-testid="marketplace-install" @click="install">{{ sameVersion ? '已是此版本' : selected.installedVersion ? '更新' : '安装' }}</button><button v-if="selected.installedVersion || pending" type="button" :disabled="props.busy || unresolved || pending" data-testid="marketplace-settings" @click="emit('settings', selected)">配置</button><button v-if="(selected.installedVersion || pending) && !uninstallArmed" type="button" :disabled="!props.canWrite || props.busy || unresolved || pending" data-testid="marketplace-uninstall" @click="uninstallArmed = true">卸载…</button></div>
          <div v-if="uninstallArmed" class="uninstall-confirm"><p>卸载不会删除原有插件配置。应用方式以宿主回执为准。</p><button type="button" :disabled="!props.canWrite || props.busy || unresolved || pending" data-testid="marketplace-uninstall-confirm" @click="uninstall">确认卸载</button><button type="button" @click="uninstallArmed = false">取消</button></div>
        </template>
        <div v-else class="builtin-actions"><p>无需安装。启停与参数保存均由现有能力配置管理。</p><button type="button" data-testid="configure-builtin" :disabled="props.busy" @click="emit('settings', selected)">{{ props.canWrite ? '管理启用与配置' : '查看配置' }}</button></div>
        <p v-if="!props.canWrite" class="read-only">只读模式：可以浏览说明与配置，不能保存变更。</p>
      </article>
    </div>
  </section>
</template>

<style scoped>
.plugin-catalog { display: grid; gap: 1rem; color: var(--text); }
.catalog-heading { display: flex; align-items: end; justify-content: space-between; gap: 1rem; }
.catalog-heading h2 { margin: .2rem 0 0; font-size: 1.3rem; }
.catalog-eyebrow { margin: 0; font-size: .75rem; letter-spacing: .12em; color: var(--accent-deep); }
.catalog-count, .catalog-scope { color: var(--text-muted); font-size: .85rem; }
.catalog-count { white-space: nowrap; font-variant-numeric: tabular-nums; }
.catalog-scope { margin: 0; line-height: 1.7; }
.catalog-filters { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: .65rem; }
.catalog-search { grid-column: span 2; }
.catalog-filters label { display: grid; gap: .35rem; font-size: .8rem; color: var(--text-muted); }
.catalog-filters input, .catalog-filters select { width: 100%; min-width: 0; min-height: 2.65rem; padding: .5rem; border: 1px solid var(--border); border-radius: var(--radius-sm, 6px); color: var(--text); background: var(--surface); }
.catalog-layout { display: grid; grid-template-columns: minmax(230px, .85fr) minmax(0, 1.6fr); gap: 1rem; align-items: start; }
.catalog-list { display: grid; gap: .55rem; }
.catalog-entry { display: grid; gap: .35rem; width: 100%; min-width: 0; text-align: left; padding: 1rem; border: 1px solid var(--border); border-left: 3px solid transparent; border-radius: var(--radius-sm, 6px); background: var(--surface); color: var(--text); cursor: pointer; }
.catalog-entry.selected { border-color: var(--accent-deep); background: var(--accent-soft); }
.catalog-entry:hover { background: var(--accent-soft); }
.catalog-entry code, .plugin-id { font-size: .75rem; color: var(--text-muted); overflow-wrap: anywhere; }
.entry-title { font-weight: 650; font-size: 1rem; }
.entry-kicker { color: var(--text-muted); font-size: .72rem; }
.entry-summary { font-size: .82rem; line-height: 1.6; }
.entry-state { font-size: .75rem; color: var(--text-muted); margin-top: .25rem; }
.catalog-detail { min-width: 0; padding: 1.25rem; border: 1px solid var(--border); border-radius: var(--radius-md, 8px); background: var(--surface-muted); }
.catalog-detail h3 { margin: .7rem 0 .35rem; font-size: 1.35rem; }
.detail-summary, .description-sections p { font-size: .88rem; line-height: 1.75; white-space: pre-line; overflow-wrap: anywhere; }
.detail-badges, .plugin-tags { display: flex; flex-wrap: wrap; gap: .4rem; }
.detail-badges span, .plugin-tags span { border: 1px solid var(--border); padding: .2rem .45rem; border-radius: 4px; font-size: .72rem; color: var(--text-muted); }
.detail-badges .reviewed { color: var(--ok-text); }
.package-state { font-size: .85rem; font-weight: 600; }
dl { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: .4rem .8rem; font-size: .8rem; }
dt { color: var(--text-muted); } dd { margin: 0; overflow-wrap: anywhere; } a { color: var(--accent-deep); }
.description-sections { border-top: 1px solid var(--border); margin-top: 1rem; }
h4 { font-size: .85rem; margin: 1rem 0 .35rem; } .description-sections p { margin: .35rem 0; }
.catalog-risk { margin-top: 1rem; padding: .1rem .85rem .65rem; background: var(--surface); border-left: 3px solid var(--warn-text); font-size: .8rem; line-height: 1.65; }
.catalog-risk p { white-space: pre-line; overflow-wrap: anywhere; }.catalog-risk ul { padding-left: 1.2rem; }
.artifact-details { margin: 1rem 0; font-size: .8rem; } .artifact-details summary { cursor: pointer; min-height: 2rem; }
.hash { font-family: var(--font-mono, monospace); word-break: break-all; }.trust { display: flex; align-items: start; gap: .5rem; font-size: .8rem; line-height: 1.6; margin: 1rem 0; }
.catalog-actions { display: flex; flex-wrap: wrap; gap: .5rem; margin-top: .8rem; }
.catalog-actions button, .builtin-actions button, .uninstall-confirm button { min-height: 2.75rem; padding: .5rem .9rem; border: 1px solid var(--border); border-radius: 6px; color: var(--text); background: var(--surface); cursor: pointer; }
.catalog-actions button:first-child, .builtin-actions button { background: var(--primary); color: var(--on-primary); border-color: var(--accent-deep); }
button:disabled { opacity: .5; cursor: not-allowed; }button:focus-visible, input:focus-visible, select:focus-visible, summary:focus-visible { outline: 2px solid var(--accent-deep); outline-offset: 3px; }
.pending-copy, .read-only, .builtin-actions p, .empty-copy { font-size: .8rem; line-height: 1.6; color: var(--text-muted); }.uninstall-confirm { margin-top: .75rem; padding: .75rem; border: 1px solid var(--warn-text); font-size: .8rem; }.uninstall-confirm button { margin-right: .5rem; }
@media (max-width: 760px) { .catalog-layout { grid-template-columns: 1fr; } .catalog-filters { grid-template-columns: repeat(2, minmax(0, 1fr)); }.catalog-list { max-height: 19rem; overflow-y: auto; }.catalog-detail { padding: 1rem; } }
@media (prefers-reduced-motion: reduce) { * { scroll-behavior: auto; } }
</style>
