<script setup lang="ts">
import { pluginTargetLabel } from '../../api/plugin-runtime'
import PluginGovernanceNotice from './PluginGovernanceNotice.vue'
import type { PluginInstallPreview } from '../../api/plugin-import'
const props = defineProps<{ preview: PluginInstallPreview; acknowledged: boolean; canConfirm: boolean; expired: boolean; busy: boolean }>()
const emit = defineEmits<{ 'update:acknowledged': [value: boolean]; confirm: []; cancel: [] }>()
const checkLabel = { passed: '通过', pending: '待确认：安装时才校验', warning: '警告', blocked: '阻止安装' }
</script>
<template>
  <section class="install-preview" data-testid="plugin-install-preview" aria-labelledby="install-preview-title">
    <div class="preview-heading"><div><p class="eyebrow">只读安装预览</p><h3 id="install-preview-title">{{ props.preview.metadata?.name || props.preview.manifest.id }}</h3></div><button type="button" @click="emit('cancel')">取消预览</button></div>
    <p>预览不会下载 ZIP、执行插件或修改配置。确认安装后默认停用业务能力，启用前仍需单独检查配置。</p>
    <dl><dt>插件 ID</dt><dd>{{ props.preview.manifest.id }}</dd><dt>当前 → 候选版本</dt><dd>{{ props.preview.installedVersion || '未安装' }} → {{ props.preview.manifest.version }}</dd><dt>来源</dt><dd>{{ props.preview.source.kind }} · {{ props.preview.source.url || '提交的技术清单' }}</dd><dt v-if="props.preview.source.revision">固定源码版本</dt><dd v-if="props.preview.source.revision" class="hash">{{ props.preview.source.revision }}</dd><dt>包 SHA-256</dt><dd class="hash" data-testid="preview-sha256">{{ props.preview.manifest.sha256 }}</dd><dt>目标</dt><dd>{{ pluginTargetLabel(props.preview.manifest) }} · API {{ props.preview.manifest.apiVersion }} · 最低宿主 {{ props.preview.manifest.minimumHostVersion || '未限制' }}</dd></dl>
    <h4>兼容与依赖检查</h4><ul class="checks"><li v-for="check in props.preview.checks" :key="check.code" :data-status="check.status"><strong>{{ checkLabel[check.status] }}</strong> · {{ check.message }}</li></ul>
    <div class="preview-columns"><section><h4>请求权限</h4><ul><li v-for="permission in props.preview.manifest.permissions" :key="permission">{{ permission }}</li><li v-if="!props.preview.manifest.permissions?.length">未声明权限；这不等于无执行风险。</li></ul><h4>声明的依赖</h4><ul><li v-for="dependency in props.preview.manifest.dependencies || []" :key="dependency.id">{{ dependency.id }} · 要求 {{ dependency.version }}（必需）</li><li v-if="!props.preview.manifest.dependencies?.length">未声明依赖</li></ul></section><section><h4>配置字段声明</h4><ul><li v-for="setting in props.preview.manifest.settings" :key="setting.key">{{ setting.label }} · {{ setting.key }} · {{ setting.type }}{{ setting.type === 'secret' ? ' · 密钥字段' : '' }}</li><li v-if="!props.preview.manifest.settings?.length">无需配置字段</li></ul><p>这里只显示字段声明，不读取或回显实际设置和密钥。</p></section></div>
    <p class="risk">{{ props.preview.trustNotice }} 默认停用不代表零代码执行：安装时加载程序集、构造函数或属性读取仍可能执行第三方代码。插件在宿主进程内运行，不是沙箱；热应用可能失败，回执也可能要求重启宿主。</p>
    <PluginGovernanceNotice :state="props.preview.governance" :source="props.preview.governanceSource" show-source />
    <p v-if="props.expired" role="status" class="risk">预览已过期，请重新预览后确认。</p>
    <p v-else-if="!props.preview.canInstall" role="status" class="risk">此候选当前不能安装，请处理阻止项后重新预览。</p>
    <label class="ack"><input type="checkbox" data-testid="preview-acknowledge" :checked="props.acknowledged" :disabled="props.busy || props.expired || !props.preview.canInstall" @change="emit('update:acknowledged', ($event.target as HTMLInputElement).checked)">我已核对以上精确版本与哈希，信任该来源并接受宿主代码执行风险。</label>
    <button type="button" class="primary" data-testid="preview-confirm" :disabled="!props.canConfirm || props.busy || props.preview.governance?.blocked" @click="emit('confirm')">确认安装此候选（默认停用）</button>
  </section>
</template>
<style scoped>
.install-preview { margin: 1rem 0; padding: 1.25rem; border: 2px solid var(--accent-deep); border-radius: 12px; background: var(--surface); color: var(--text); scroll-margin-top: 1rem; }
.preview-heading { display: flex; gap: 1rem; justify-content: space-between; align-items: start; } h3 { margin: .25rem 0; } h4 { margin-bottom: .4rem; }
p, li { font-size: .85rem; line-height: 1.65; overflow-wrap: anywhere; } dl { display: grid; grid-template-columns: max-content minmax(0,1fr); gap: .5rem 1rem; font-size: .85rem; } dt { color: var(--text-muted); } dd { margin: 0; overflow-wrap: anywhere; }.hash { font-family: monospace; word-break: break-all; }
.preview-columns { display: grid; grid-template-columns: 1fr 1fr; gap: 1.25rem; }.checks { padding-left: 1.1rem; }[data-status="blocked"], [data-status="warning"], .risk { color: var(--warn-text); }[data-status="passed"] { color: var(--ok-text); }.ack { display: flex; gap: .6rem; margin: 1rem 0; font-size: .9rem; line-height: 1.6; }
button { min-height: 44px; border: 1px solid var(--border); border-radius: 7px; padding: .5rem .85rem; color: var(--text); background: var(--surface-muted); }.primary { background: var(--primary); color: var(--on-primary); }button:disabled { opacity: .5; }button:focus-visible, input:focus-visible { outline: 2px solid var(--accent-deep); outline-offset: 3px; }@media(max-width: 640px) { .preview-columns { grid-template-columns: 1fr; }dl { grid-template-columns: 1fr; gap: .2rem; }dd { margin-bottom: .5rem; }.install-preview { padding: .8rem; } }
</style>