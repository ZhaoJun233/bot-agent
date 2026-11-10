import type { ZbaPluginCatalogItem } from '../../src/api/zba'
import type { PluginLifecyclePreview, PluginLifecyclePreviewRequest } from '../../src/api/plugin-lifecycle'
import { lifecycleChanges } from '../../src/api/plugin-lifecycle-diff'
import { syntheticManifest } from './provider-marketplace'
export const currentManifest = { ...syntheticManifest, version: '1.0.0' }
export const nextManifest = { ...syntheticManifest, version: '2.0.0', sha256: 'b'.repeat(64) }
export const previousManifest = { ...syntheticManifest, version: '0.9.0', sha256: 'c'.repeat(64) }
export function installedRow(overrides: Partial<ZbaPluginCatalogItem> = {}): ZbaPluginCatalogItem {
  return { manifest: currentManifest, installedVersion: currentManifest.version, previousManifest, pendingManifest: null,
    enabled: true, status: 'installed', restartRequired: false, ...overrides }
}
export function lifecyclePreview(request: PluginLifecyclePreviewRequest, overrides: Partial<PluginLifecyclePreview> = {}): PluginLifecyclePreview {
  const target = request.kind === 'update'
    ? request.candidate.origin === 'repository' ? { ...nextManifest, version: request.candidate.version } : request.candidate.manifest
    : request.kind === 'rollback' ? previousManifest : null
  const value: PluginLifecyclePreview = { previewId: 'lifecycle-example', expiresAt: new Date(Date.now() + 60_000).toISOString(),
    revision: request.expectedRevision, kind: request.kind, recovery: 'none', pluginId: request.pluginId,
    currentManifest, targetManifest: target, pendingManifest: null,
    targetSource: target ? { origin: request.kind === 'update' ? request.candidate.origin : 'installed', url: null, revision: null } : null,
    metadata: null, enabled: true, currentStatus: 'installed', restartRequired: false, canApply: true,
    changes: [], checks: [{ code: 'manifest', status: 'passed', message: '完整技术清单有效' }],
    preservation: { settings: true, userData: true, enabledIntent: true }, trustNotice: '升级可能执行第三方插件初始化代码，不是沙箱。', ...overrides }
  if (!overrides.changes) value.changes = lifecycleChanges(value.currentManifest, value.targetManifest)
  return value
}
export function updateRequest(): PluginLifecyclePreviewRequest {
  return { kind: 'update', pluginId: currentManifest.id, expectedRevision: 'rev-1', candidate: { origin: 'catalog', manifest: nextManifest } }
}