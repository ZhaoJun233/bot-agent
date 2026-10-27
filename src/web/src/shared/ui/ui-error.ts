import { ZbaNetworkError, ZbaRequestError } from '../../api/zba'

export interface UiError {
  code: string
  message: string
  status?: number
  requestId?: string
}

/**
 * 把网络层错误归一成 UI 文案。
 *
 * 只使用安全错误码/状态码；不携带请求体、令牌、秘密或后端原始消息之外的细节。
 */
export function toUiError(cause: unknown): UiError {
  if (cause instanceof ZbaRequestError) {
    // 2xx 但响应体无法验证：服务端可能已经处理，只是回执读不懂——不能写成"失败"。
    if (cause.status >= 200 && cause.status < 300) {
      return {
        code: cause.code,
        message: '服务端已响应，但回执无法验证：结果未知，请重新读取确认后再操作。',
        status: cause.status,
        requestId: cause.requestId
      }
    }
    const messages: Record<number, string> = {
      400: '请求无效。',
      401: '令牌无效或已过期，请重新输入。',
      403: '当前令牌没有该操作权限。',
      404: '资源不存在，或记录已不可恢复。',
      409: '数据版本已变化，请先读取最新状态。',
      429: '认证失败次数过多，请稍后重试。',
      500: 'ZBA 服务内部错误。',
      503: cause.code === 'zba_not_configured'
        ? '后端未配置 ZBA 访问令牌（BOTAGENT_ZBA_TOKEN）。'
        : 'ZBA 服务暂不可用。'
    }
    return {
      code: cause.code,
      message: messages[cause.status] ?? 'ZBA 请求失败（HTTP ' + cause.status + '）。',
      status: cause.status,
      requestId: cause.requestId
    }
  }

  if (cause instanceof ZbaNetworkError) {
    // 超时：请求可能已经到达服务端，结果未知。
    if (cause.detail === 'timeout') {
      return { code: 'timeout', message: '请求超时：结果未知，请重新读取确认后再操作，不要直接重复提交。' }
    }
    return { code: 'network_unavailable', message: '无法连接 ZBA 服务，请检查地址与网络。' }
  }

  return { code: 'unexpected_error', message: '请求失败，请稍后再试。' }
}
