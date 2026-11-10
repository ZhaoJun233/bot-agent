<script setup lang="ts">
import PluginGovernanceNotice from './PluginGovernanceNotice.vue'
import { computed } from 'vue'
import type { PluginLifecyclePreview } from '../../api/plugin-lifecycle'
import { lifecycleManifest } from '../../api/plugin-lifecycle-diff'
const props = defineProps<{ preview: PluginLifecyclePreview; acknowledged: boolean; expired: boolean; canConfirm: boolean }>()
const emit = defineEmits<{ 'update:acknowledged': [value: boolean]; confirm: []; cancel: [] }>()
const action = computed(() => ({ update: '升级', rollback: '回退', uninstall: '卸载' })[props.preview.kind])
const names: Record<string, string> = { id: '插件 ID', version: '版本', sha256: 'SHA-256', downloadUrl: '下载地址', entryAssembly: '入口程序集', entryType: '入口类型', apiVersion: '插件 API', targetFramework: '目标框架', runtime: 'Python 运行时与工具声明', minimumHostVersion: '最低宿主版本', permissions: '权限', dependencies: '依赖（精确版本）', settings: '设置定义' }
function pretty(value: string | null): string {
  if (value === null) return '（不存在）'
  try { const parsed: unknown = JSON.parse(value); return typeof parsed === 'string' ? parsed : JSON.stringify(parsed, null, 2) } catch { return value }
}
</script>
<template>
  <section class="panel lifecycle-preview" data-testid="lifecycle-preview" aria-label="插件变更预览">
    <header><h3><span data-testid="lifecycle-kind">{{ action }}</span>预览 · {{ preview.metadata?.name || preview.pluginId }}</h3><span class="tag">只读预览 · 尚未变更</span></header>
    <p class="subtle">{{ preview.pluginId }} · 当前状态 {{ preview.currentStatus }} · {{ preview.enabled ? '已启用' : '已停用' }}<span v-if="preview.restartRequired"> · 等待重启</span></p>
    <div class="lifecycle-comparison">
      <div><h4>{{ preview.recovery === 'failed-install' ? '未生效安装候选' : '当前版本' }}</h4><strong data-testid="lifecycle-current-version">{{ preview.currentManifest.version }}</strong><p class="subtle">当前 SHA-256</p><code>{{ preview.currentManifest.sha256 }}</code></div>
      <div><h4>目标版本</h4><strong data-testid="lifecycle-target-version">{{ preview.targetManifest?.version || '无（卸载或没有可用的上一版本）' }}</strong><template v-if="preview.targetManifest"><p class="subtle">目标 SHA-256</p><code>{{ preview.targetManifest.sha256 }}</code></template></div>
    </div>
    <p v-if="preview.targetSource" class="subtle">来源：{{ { catalog: '市场目录', repository: '指定仓库', json: 'JSON 清单', installed: preview.recovery === 'failed-pending' ? '本机当前工作版本' : '本机上一版本' }[preview.targetSource.origin] }}<span v-if="preview.targetSource.url"> · {{ preview.targetSource.url }}</span><span v-if="preview.targetSource.revision"> · {{ preview.targetSource.revision }}</span></p>
    <div data-testid="lifecycle-changes">
      <h4>完整技术差异</h4><p v-if="!preview.changes.length" class="subtle">没有技术字段变化。</p>
      <div v-for="change in preview.changes" :key="change.field" class="lifecycle-diff">
        <h5>{{ names[change.field] || change.field }} <code>{{ change.field }}</code></h5>
        <div class="lifecycle-comparison"><div><span class="subtle">变更前</span><pre>{{ pretty(change.before) }}</pre></div><div><span class="subtle">变更后</span><pre>{{ pretty(change.after) }}</pre></div></div>
      </div>
    </div>
    <details><summary>{{ preview.recovery === 'failed-install' ? '完整未生效候选 / 目标技术清单' : '完整当前 / 目标技术清单' }}</summary><div class="lifecycle-comparison"><pre>{{ JSON.stringify(lifecycleManifest(preview.currentManifest), null, 2) }}</pre><pre>{{ preview.targetManifest ? JSON.stringify(lifecycleManifest(preview.targetManifest), null, 2) : '无目标清单' }}</pre></div></details>
    <p v-if="preview.recovery === 'failed-install'" class="lifecycle-warning" role="note">移除未生效安装候选；该版本从未成为工作中的 Active。仅清理失败安装的待处理状态，设置和用户数据保留。</p>
    <div v-if="preview.recovery === 'failed-pending'" class="lifecycle-warning" role="note">
      <strong>放弃失败候选，恢复当前可用版本 {{ preview.currentManifest.version }}</strong>
      <p>本次恢复仅放弃未成功应用的候选，不是数据回滚；不恢复或删除设置及用户数据。</p>
      <details v-if="preview.pendingManifest"><summary>将放弃的失败候选 {{ preview.pendingManifest.version }} · {{ preview.pendingManifest.sha256 }}</summary><pre>{{ JSON.stringify(lifecycleManifest(preview.pendingManifest), null, 2) }}</pre></details>
    </div>
    <p data-testid="lifecycle-preservation">保留现有设置、用户数据和启用意图；不会自动启用或停用。卸载只停止插件，不删除用户数据。设置定义差异不代表存储值被修改。</p>
    <ul class="lifecycle-checks"><li v-for="(check, index) in preview.checks" :key="index" :class="{ 'lifecycle-blocked': check.status === 'blocked' || check.status === 'warning' }"><strong>{{ { passed: '已通过', pending: '待确认时校验', warning: '警告', blocked: '已阻止' }[check.status] }}</strong> · {{ check.message }} <code>{{ check.code }}</code></li></ul>
    <div v-if="expired || !preview.canApply" class="error-notice" role="alert" data-testid="lifecycle-error">{{ expired ? '预览已过期，确认已失效；请重新预览。' : '当前变更被阻止，请先处理上述原因并重新预览。' }}</div>
    <p class="lifecycle-warning">{{ preview.trustNotice }}</p>
    <PluginGovernanceNotice :state="preview.currentGovernance" :source="preview.governanceSource" installed show-source />
    <PluginGovernanceNotice v-if="preview.targetManifest" :state="preview.targetGovernance ?? undefined" />
    <p class="subtle">插件不是沙箱。停用不代表零代码执行：加载时仍可能构造实例并执行初始化代码。预览不下载或执行包；确认后才执行对应校验与变更。</p>
    <label class="lifecycle-ack"><input data-testid="lifecycle-acknowledge" type="checkbox" :checked="acknowledged" :disabled="expired || !preview.canApply" @change="emit('update:acknowledged', ($event.target as HTMLInputElement).checked)" />我已核对本次版本、完整技术差异和风险，仅确认这一次{{ action }}。</label>
    <div class="row wrap"><button type="button" class="primary" data-testid="lifecycle-confirm" :disabled="!canConfirm || preview.targetGovernance?.blocked" @click="emit('confirm')">确认{{ action }}</button><button type="button" @click="emit('cancel')">取消预览</button></div>
  </section>
</template>
<style scoped>
.lifecycle-preview { display: grid; gap: 12px; padding: 16px; min-width: 0; }
.lifecycle-preview h3, .lifecycle-preview h4, .lifecycle-preview h5, .lifecycle-preview p { margin: 0; }
.lifecycle-preview header { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.lifecycle-comparison { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.lifecycle-comparison > div { min-width: 0; }
.lifecycle-preview code, .lifecycle-preview p { overflow-wrap: anywhere; }
.lifecycle-preview pre { white-space: pre-wrap; overflow-wrap: anywhere; word-break: break-word; font-size: 12px; margin: 6px 0; }
.lifecycle-diff { border-top: 1px solid var(--border); margin-top: 10px; padding-top: 10px; }
.lifecycle-ack { display: flex; align-items: flex-start; gap: 8px; }
.lifecycle-warning, .lifecycle-blocked { color: var(--bad-text); }
.lifecycle-checks { padding-left: 20px; margin: 0; }
@media (max-width: 640px) { .lifecycle-comparison { grid-template-columns: minmax(0, 1fr); } }
</style>