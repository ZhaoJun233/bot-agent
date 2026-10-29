<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { filterPluginCatalog, pluginPackageStatus, safeGithubLink, type PluginCatalogEntry } from './plugin-catalog'
const props = defineProps<{
  entries: PluginCatalogEntry[]
  canWrite: boolean
  busy?: boolean
  loading?: boolean
}>()
const emit = defineEmits<{
  install: [entry: PluginCatalogEntry]
  uninstall: [entry: PluginCatalogEntry]
  settings: [entry: PluginCatalogEntry]
}>()
const search = ref('')
const category = ref('all')
const selectedId = ref<string | null>(null)
const trusted = ref(false)
const uninstallArmed = ref(false)
const categories = computed(() => [...new Set(props.entries.map((entry) => entry.category))].sort())
const entries = computed(() => filterPluginCatalog(props.entries, search.value, category.value))
const selected = computed(() => entries.value.find((entry) => entry.id === selectedId.value) ?? entries.value[0])
const repository = computed(() => safeGithubLink(selected.value?.repository))
const pending = computed(() => Boolean(selected.value?.pendingVersion || selected.value?.pendingUninstall))
const sameVersion = computed(() => selected.value?.installedVersion === selected.value?.manifest.version)
watch(() => [selected.value?.id, selected.value?.manifest.sha256, selected.value?.manifest.version], () => { trusted.value = false; uninstallArmed.value = false })
function install(): void {
  if (!selected.value || !props.canWrite || props.busy || pending.value || sameVersion.value || !trusted.value) return
  emit('install', selected.value)
}
function uninstall(): void {
  if (!selected.value?.installedVersion || !props.canWrite || props.busy || pending.value || !uninstallArmed.value) return
  emit('uninstall', selected.value)
}
</script>
<template>
  <div class="catalog-toolbar">
    <label class="search">搜索插件<input v-model="search" type="search" placeholder="按名称、用途或关键词查找" data-testid="marketplace-search"></label>
    <label>分类<select v-model="category" data-testid="marketplace-category"><option value="all">全部分类</option><option v-for="item in categories" :key="item" :value="item">{{ item }}</option></select></label>
  </div>
  <p v-if="props.loading" role="status" class="muted">正在读取插件目录…</p>
  <p v-else-if="!entries.length" class="catalog-empty" data-testid="marketplace-empty">没有匹配的插件。调整搜索，或在「指定数据源」栏填写符合规范的 GitHub 发布地址。</p>
  <div v-else class="catalog-layout">
    <nav class="catalog-list" aria-label="插件目录" data-testid="marketplace-list">
      <button v-for="entry in entries" :key="entry.id" type="button" class="catalog-entry" :class="{ selected: entry.id === selected?.id }" :aria-pressed="entry.id === selected?.id" :data-testid="'marketplace-item-' + entry.id" @click="selectedId = entry.id">
        <span class="catalog-entry-head"><strong>{{ entry.name }}</strong><span>{{ entry.category }}</span></span>
        <span class="catalog-entry-summary">{{ entry.summary }}</span>
        <span class="catalog-entry-status">{{ pluginPackageStatus(entry) }}</span>
      </button>
    </nav>
    <section v-if="selected" class="catalog-detail" aria-label="插件详情" data-testid="marketplace-detail">
      <p class="eyebrow">{{ selected.category }}</p><h3>{{ selected.name }}</h3><p class="muted">{{ selected.summary }}</p>
      <dl class="package-facts">
        <div><dt>插件 ID</dt><dd>{{ selected.id }}</dd></div>
        <div v-if="selected.author"><dt>作者</dt><dd>{{ selected.author }}</dd></div>
        <div v-if="selected.tags?.length"><dt>标签</dt><dd>{{ selected.tags.join(' · ') }}</dd></div>
        <div><dt>固定版本</dt><dd data-testid="marketplace-version">{{ selected.manifest.version }}</dd></div>
        <div><dt>兼容性</dt><dd>API {{ selected.manifest.apiVersion }} · {{ selected.manifest.targetFramework }}<span v-if="selected.manifest.minimumHostVersion"> · 宿主 ≥ {{ selected.manifest.minimumHostVersion }}</span></dd></div>
        <div><dt>状态</dt><dd data-testid="marketplace-package-state">{{ pluginPackageStatus(selected) }}</dd></div>
        <div v-if="repository"><dt>来源</dt><dd><a :href="repository" target="_blank" rel="noopener noreferrer" data-testid="marketplace-source-link">GitHub 仓库 ↗</a></dd></div>
      </dl>
      <div class="integrity"><strong>ZIP SHA-256</strong><code data-testid="marketplace-sha256">{{ selected.manifest.sha256 }}</code><p>安装固定版本并校验完整 ZIP；哈希验证不代表代码本身安全。</p></div>
      <section class="permissions" aria-labelledby="marketplace-permissions-title"><h4 id="marketplace-permissions-title">声明的权限</h4>
        <ul v-if="selected.manifest.permissions?.length" data-testid="marketplace-permissions"><li v-for="permission in selected.manifest.permissions" :key="permission">{{ permission }}</li></ul>
        <p v-else class="muted">未声明权限；不代表插件没有访问能力。</p>
      </section>
      <p v-if="selected.manifest.dependencies?.length" class="muted">依赖：{{ selected.manifest.dependencies.map((item) => item.id + ' @ ' + item.version).join('、') }}。不会自动安装未确认的依赖。</p>
      <section class="trust-warning" data-testid="marketplace-trust-warning">
        <h4>仅安装你信任的代码</h4><p>插件在宿主进程内运行，<strong>不是沙箱</strong>。权限是声明信息，不是隔离边界；插件可能访问宿主数据和网络。</p>
        <label v-if="!sameVersion && !pending" class="trust-check"><input v-model="trusted" type="checkbox" data-testid="marketplace-trust" :disabled="!props.canWrite || props.busy">我信任此来源，并已核对固定版本、SHA-256 与权限声明。</label>
      </section>
      <p class="muted">外部插件默认停用。安装、更新或卸载需要重启后生效；此处不会自动重启。</p>
      <div class="catalog-actions">
        <button v-if="!sameVersion" type="button" class="primary" data-testid="marketplace-install" :disabled="!props.canWrite || props.busy || pending || !trusted" @click="install">{{ props.busy ? '处理中…' : selected.installedVersion ? '暂存此版本更新' : '安装此固定版本' }}</button>
        <button v-if="selected.installedVersion || selected.pendingVersion" type="button" data-testid="marketplace-settings" :disabled="props.busy" @click="emit('settings', selected)">设置与启用</button>
        <button v-if="selected.installedVersion && !uninstallArmed" type="button" data-testid="marketplace-uninstall" :disabled="!props.canWrite || props.busy || pending" @click="uninstallArmed = true">卸载…</button>
      </div>
      <div v-if="uninstallArmed" class="trust-warning" role="alert" data-testid="marketplace-uninstall-confirmation"><p>确认暂存卸载？服务不会自动重启。重启后的状态以宿主为准。</p><button type="button" data-testid="marketplace-uninstall-confirm" :disabled="!props.canWrite || props.busy || pending" @click="uninstall">确认卸载</button><button type="button" @click="uninstallArmed = false">取消</button></div>
    </section>
  </div>
</template>
<style scoped>
.catalog-toolbar { display: flex; gap: 12px; align-items: end; margin: 16px 0; }
.catalog-toolbar label { display: grid; gap: 6px; font-size: 13px; font-weight: 600; }
.catalog-toolbar .search { flex: 1; }
.catalog-toolbar input, .catalog-toolbar select { width: 100%; min-width: 0; }
.catalog-layout { display: grid; grid-template-columns: minmax(220px, 1fr) minmax(0, 1.65fr); gap: 20px; align-items: start; }
.catalog-list { display: grid; gap: 6px; }
.catalog-entry { display: grid; gap: 8px; text-align: left; width: 100%; background: var(--surface); color: var(--text); border: 1px solid var(--border); border-radius: 8px; padding: 14px; min-width: 0; }
.catalog-entry.selected { border-color: var(--accent-deep); background: var(--accent-soft, #eff7fc); }
.catalog-entry-head { display: flex; gap: 8px; align-items: baseline; justify-content: space-between; }
.catalog-entry-head > span, .catalog-entry-status { font-size: 11px; color: var(--text-muted); }
.catalog-entry-summary { color: var(--text-muted); font-size: 12px; line-height: 1.5; font-weight: 400; }
.catalog-detail { border-left: 1px solid var(--border); padding-left: 20px; min-width: 0; overflow-wrap: break-word; }
.catalog-detail h3 { font-size: 20px; margin: 0 0 8px; }
.eyebrow { color: var(--accent-deep); font-size: 11px; font-weight: 700; margin: 0 0 6px; }
.muted { color: var(--text-muted); font-size: 13px; line-height: 1.65; }
.package-facts { display: grid; gap: 8px; font-size: 12px; }
.package-facts > div { display: grid; grid-template-columns: 66px minmax(0, 1fr); gap: 12px; }
.package-facts dt { color: var(--text-muted); }.package-facts dd { margin: 0; }
.integrity { background: var(--surface-alt, #f5f7f9); border-radius: 6px; padding: 12px; font-size: 11px; }
.integrity code { display: block; word-break: break-all; margin: 7px 0; }.integrity p { color: var(--text-muted); margin: 0; line-height: 1.5; }
.permissions h4, .trust-warning h4 { margin: 0 0 8px; font-size: 13px; }.permissions { margin-top: 18px; }.permissions ul { display: flex; flex-wrap: wrap; list-style: none; gap: 6px; padding: 0; }.permissions li { font-size: 11px; padding: 4px 7px; background: var(--surface-alt, #f5f7f9); border-radius: 4px; }
.trust-warning { border: 1px solid #dfbf7e; background: #fff8e9; color: #6d501b; border-radius: 8px; padding: 12px; margin: 14px 0; font-size: 12px; line-height: 1.7; }.trust-warning p { margin: 0 0 8px; }
.trust-check { display: flex; align-items: flex-start; gap: 8px; font-weight: 600; }.trust-check input { flex: none; margin-top: 4px; }
.catalog-actions { display: flex; flex-wrap: wrap; gap: 8px; }.catalog-empty { padding: 28px 0; font-size: 13px; color: var(--text-muted); }
@media (max-width: 700px) { .catalog-layout { grid-template-columns: 1fr; }.catalog-detail { border-left: 0; border-top: 1px solid var(--border); padding: 18px 0 0; }.catalog-list { max-height: 220px; overflow-y: auto; }.catalog-toolbar { flex-wrap: wrap; }.catalog-toolbar .search { min-width: 180px; }.catalog-actions > button { flex: 1 1 auto; } }
</style>
