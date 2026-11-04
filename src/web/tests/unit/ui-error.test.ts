import { describe, expect, it } from 'vitest'
import { ZbaRequestError } from '../../src/api/zba'
import { toUiError } from '../../src/shared/ui/ui-error'

describe('toUiError', () => {
  it('未知 HTTP 状态使用固定安全文案，不透传后端消息', () => {
    const error = toUiError(new ZbaRequestError(502, 'http_error', 'synthetic upstream payload with internals', undefined, 'request-synthetic-1'))
    expect(error.message).toBe('ZBA 请求失败（HTTP 502）。')
    expect(error.message).not.toContain('synthetic upstream payload')
    expect(error).toMatchObject({ code: 'http_error', status: 502, requestId: 'request-synthetic-1' })
  })
})
