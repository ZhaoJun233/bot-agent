import type { ZbaConfigurationReadResult, ZbaProviderConfigurationView, ZbaReadField } from '../../src/api/zba'
import editable from './configuration-read-editable-result.json'
import type { PluginPackageManifest, PluginSettingDefinition } from '../../src/features/capabilities/plugin-manifest'

export function textField(value: string, name = false): ZbaReadField<string> {
  return {
    visibility: name ? 2 : 1, display: 'masked', ...(name ? { nameRaw: value } : { value }),
    rule: { id: 1, effect: 0, canClear: false, editable: true, legacyNeedsReview: false },
    userMode: 2, effectiveSource: 1, applyState: 1
  }
}
export function syntheticProvider(overrides: Partial<ZbaProviderConfigurationView> = {}): ZbaProviderConfigurationView {
  return {
    key: 'provider-sample', name: textField('示例提供商', true), baseUrl: textField('https://example.com/v1'),
    modelName: textField('model-example'), priority: 0, desiredEnabled: false, credential: 0, runtimeApplyState: 3,
    ...overrides
  }
}
export function providerSnapshot(providers: ZbaProviderConfigurationView[] = [syntheticProvider()], version = '1'): ZbaConfigurationReadResult {
  const snapshot = structuredClone(editable) as unknown as ZbaConfigurationReadResult
  snapshot.version = version
  if (!snapshot.view) throw new Error('Synthetic configuration fixture must have a view')
  snapshot.view.registered.providers = providers
  return snapshot
}
export const syntheticSettings: PluginSettingDefinition[] = [
  { key: 'caption', label: '标题', type: 'string', defaultValue: '示例' },
  { key: 'enabled', label: '主动工作', type: 'boolean', defaultValue: 'false' },
  { key: 'limit', label: '条数', type: 'number', defaultValue: '5', required: true },
  { key: 'secret', label: '密钥', type: 'secret' },
  { key: 'format', label: '格式', type: 'select', choices: ['short', 'long'], defaultValue: 'short' }
]
export const syntheticManifest: PluginPackageManifest = {
  id: 'example.plugin', version: '1.0.0', entryAssembly: 'Example.Plugin.dll', entryType: 'Example.Plugin',
  sha256: 'a'.repeat(64), downloadUrl: 'https://example.com/fixture.zip', apiVersion: 1,
  targetFramework: 'net8.0', permissions: ['network', 'messages.read'], settings: syntheticSettings
}
