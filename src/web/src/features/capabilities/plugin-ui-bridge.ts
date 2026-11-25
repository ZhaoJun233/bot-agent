import { defineComponent, h, markRaw } from 'vue'
import type { PluginUiBridge, UiOperation } from '../../../../../contracts/ui'
import { ZbaRequestError, type ZbaClient } from '../../api/zba'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError } from '../../shared/ui/ui-error'
import { useDraftProtection } from '../../shared/ui/draft-protection'
import { newNonce } from '../../shared/nonce'
import { beginOperation, markOperation, pendingOperations, type PendingOperationKind } from '../../shared/sync/pending-operations'
import RegisteredPluginSwitch from './RegisteredPluginSwitch.vue'

interface EditorOwner {
  client: ZbaClient; mode: 'read' | 'admin'; writesEnabled: boolean; active: boolean
}

/** The current presentation may change; late receipts always settle against the captured owner. */
export function createPluginUiBridge(current: () => EditorOwner, pluginKey: string, kind: PendingOperationKind): PluginUiBridge {
  const owner = current().client
  const projections = new WeakMap<object, UiOperation>()
  const list = () => pendingOperations(owner, kind).map(record => {
    let value = projections.get(record)
    if (!value) {
      value = Object.freeze({ key: record.key, target: record.target, meta: record.meta, state: record.state })
      projections.set(record, value)
    }
    return value
  })
  const PluginSwitch = markRaw(defineComponent({
    inheritAttrs: false,
    props: { blocked: { type: Boolean, required: true }, runtimeState: Number },
    setup(props, { attrs, slots }) {
      return () => {
        const { client, mode, writesEnabled, active } = current()
        return h(RegisteredPluginSwitch, { ...attrs, ...props, client, mode, writesEnabled, active, pluginKey }, slots)
      }
    }
  }))
  return Object.freeze({
    components: Object.freeze({ ManagementDialog: markRaw(ManagementDialog), ErrorNotice: markRaw(ErrorNotice), PluginSwitch }),
    operations: Object.freeze({
      list,
      begin: (input: Parameters<PluginUiBridge['operations']['begin']>[0]) => {
        if (current().client !== owner || current().mode !== 'admin' || !current().writesEnabled || !current().active)
          throw new Error('plugin_ui_scope_unavailable')
        beginOperation(owner, { ...input, kind })
      },
      mark: (key: string, state: 'unknown' | 'settled') => {
        if (!pendingOperations(owner, kind).some(record => record.key === key)) throw new Error('plugin_ui_operation_unavailable')
        markOperation(owner, key, state)
      }
    }),
    requestError: (cause: unknown) => cause instanceof ZbaRequestError ? { status: cause.status, code: cause.code } : null,
    toUiError, newOperationKey: newNonce, protectDraft: useDraftProtection
  })
}
