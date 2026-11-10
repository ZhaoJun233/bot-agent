import { classifySubmissionFailure, ZbaRequestError } from '../../api/zba'

const preconditionRejections = new Set([
  'plugin_revision_conflict', 'plugin_settings_revision_conflict', 'plugin_preview_required',
  'plugin_preview_refresh_required', 'plugin_governance_revoked'
])

export function isPluginWriteOutcomeUnknown(cause: unknown): boolean {
  if (cause instanceof ZbaRequestError) {
    // This contract means staged/saved, with a recorded apply failure.
    if (cause.status === 503 && cause.code === 'plugin_reload_failed') return false
    if (cause.status === 409 && preconditionRejections.has(cause.code)) return false
  }
  return classifySubmissionFailure(cause) === 'unknown'
}
