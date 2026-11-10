import { defineComponent } from 'vue'
import type { PluginInstallPreview, PluginPreviewRequest, PluginSourceDiscovery } from '../../src/api/plugin-import'
import { syntheticManifest } from './provider-marketplace'

/** Mirrors the candidate/revision binding enforced by the server preview endpoint. */
// VTU bypasses its Teleport transform cache and remounts teleport:true stubs on each update.
// Interaction tests use a stable shell; modal/focus suites and headed E2E exercise the real dialog.
export const marketDialogShell = defineComponent({
  props: ['testId', 'active', 'title'], emits: ['close'],
  template: '<section :data-testid="testId"><slot name="eyebrow" /><h2>{{ title }}</h2><button :data-testid="testId + \'-close\'" @click="$emit(\'close\')">关闭</button><slot /></section>'
})

export function previewFor(request: PluginPreviewRequest): PluginInstallPreview {
  return installPreview({
    revision: request.expectedRevision,
    manifest: request.origin === 'repository' ? { ...syntheticManifest, id: request.pluginId, version: request.version } : request.manifest,
    source: { kind: request.origin },
  })
}
export function discovery(overrides: Partial<PluginSourceDiscovery> = {}): PluginSourceDiscovery {
  return { discoveryId: 'discovery-example', sourceUrl: 'https://github.com/example-org/example-plugin', sourceRevision: 'b'.repeat(40), expiresAt: new Date(Date.now() + 60_000).toISOString(), candidates: [
    { pluginId: syntheticManifest.id, version: '1.0.0' }, { pluginId: syntheticManifest.id, version: '2.0.0' }, { pluginId: 'example.second', version: '1.0.0' }
  ], ...overrides }
}
export function installPreview(overrides: Partial<PluginInstallPreview> = {}): PluginInstallPreview {
  return { previewId: 'preview-example', expiresAt: new Date(Date.now() + 60_000).toISOString(), revision: 'rev-1', manifest: syntheticManifest, source: { kind: 'catalog' }, enabledAfterInstall: false, canInstall: true,
    checks: [{ code: 'compatibility', status: 'passed', message: '兼容性通过' }, { code: 'archive', status: 'pending', message: 'ZIP 与哈希待确认安装时验证' }], trustNotice: '仅确认你信任的来源。', ...overrides }
}