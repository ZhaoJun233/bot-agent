import type { ZbaConfigurationView, ZbaFieldError } from '../../api/zba'
import { fieldLabel } from './field-labels'

export interface ConfigurationFieldError { path?: string; message: string }
export const providerErrorPath = (key: string, field = 'name'): string => 'provider:' + JSON.stringify([key, field])

/** Resolve numeric wire addresses structurally; object errors never mark a scalar with the same id. */
export function configurationFieldErrors(errors: ZbaFieldError[], view?: ZbaConfigurationView): ConfigurationFieldError[] {
  return errors.map(error => {
    const address = error.address as ZbaFieldError['address'] & { objectKey?: unknown }
    const object = address.object ?? address.objectKey
    if (view && object == null) {
      for (const section of ['behavior', 'model', 'agent', 'platform', 'voice', 'music', 'reporting'] as const) {
        for (const [key, field] of Object.entries(view[section])) {
          if (field.rule.id === address.field) return { path: section + '.' + key, message: fieldLabel(key) + '：' + error.code }
        }
      }
    }
    const objectRecord = typeof object === 'object' && object !== null ? object as Record<string, unknown> : null
    const key = typeof object === 'string' ? object : objectRecord?.key
    if (view && typeof key === 'string' && (!objectRecord || objectRecord.kind === 'provider' || objectRecord.$type === 'provider')) {
      const provider = view.registered.providers.find(provider => provider.key === key)
      if (provider) {
        for (const field of ['name', 'baseUrl', 'modelName'] as const) {
          if (provider[field].rule.id === address.field) return { path: providerErrorPath(key, field), message: key + ' / ' + (field === 'name' ? '名称' : fieldLabel(field)) + '：' + error.code }
        }
      }
    }
    return { message: '字段 ' + address.field + (typeof key === 'string' ? '（' + key + '）' : '') + '：' + error.code }
  })
}

export function localFieldErrors(errors: string[]): ConfigurationFieldError[] {
  return errors.map(message => {
    const cut = message.indexOf('：')
    const path = cut > 0 ? message.slice(0, cut) : undefined
    return { path, message }
  })
}
