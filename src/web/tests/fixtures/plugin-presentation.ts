import type { PluginPresentationMetadata, ZbaBuiltinPlugin } from '../../src/api/plugin-presentation'
import type { ZbaPluginCatalogItem } from '../../src/api/zba'
import { syntheticManifest } from './provider-marketplace'

export function presentation(overrides: Partial<PluginPresentationMetadata> = {}): PluginPresentationMetadata {
  return {
    name: '示例问候', summary: '发送示例问候消息', description: '用合成消息展示扩展能力。', category: '互动',
    author: '示例维护者', repository: 'https://github.com/example-org/example-plugin', license: 'MIT',
    usage: '发送示例指令后触发。', configuration: '先配置标题，再启用。', compatibility: '.NET 10 · Plugin API v1',
    changelog: '1.0.0：初始示例。', maintenance: '由示例维护者维护。', permissionNotes: '需要发送消息权限。',
    riskNotice: '会在宿主进程运行，不是沙箱。', tags: ['问候', '演示'], runtime: 'dotnet', sourceKind: 'official',
    review: { status: 'reviewed', version: syntheticManifest.version, sha256: syntheticManifest.sha256,
      reviewer: '示例审核者', reviewedAt: '2026-01-01T00:00:00Z', sourceRevision: 'b'.repeat(40) }, ...overrides
  }
}
export function builtin(overrides: Partial<ZbaBuiltinPlugin> = {}): ZbaBuiltinPlugin {
  return { pluginId: 'preset.feature.websearch', version: '1.0.0', enabled: false,
    metadata: presentation({ name: '联网搜索', summary: '检索公开网络信息', category: '信息查询', tags: ['搜索', '联网'],
      runtime: 'builtin', sourceKind: 'builtin', review: { status: 'unreviewed', version: '1.0.0', sha256: null } }), ...overrides }
}
export function packageItem(overrides: Partial<ZbaPluginCatalogItem> = {}): ZbaPluginCatalogItem {
  return { manifest: syntheticManifest, metadata: presentation(), enabled: false, restartRequired: false, status: 'available', ...overrides }
}
