import type { ZbaProviderProtocol } from '../../api/zba'

/** Stable form IDs map to the numeric ZBA contract, never brand names. */
export const providerProtocols = [
  { id: 'openai-chat', wire: 0, label: 'OpenAI Chat Completions', path: '/v1', hint: '兼容 OpenAI 的服务请选择此协议；填写服务商提供的 API 根地址。' },
  { id: 'openai-responses', wire: 1, label: 'OpenAI Responses', path: '/v1', hint: '用于明确支持 Responses API 的服务，不自动降级为 Chat Completions。' },
  { id: 'anthropic-messages', wire: 2, label: 'Anthropic Messages', path: '/v1', hint: '使用 Messages API，可在高级选项设置 Anthropic 版本。' },
  { id: 'gemini-generate-content', wire: 3, label: 'Gemini Generate Content', path: '/v1beta', hint: '使用原生 generateContent 协议，不是 OpenAI 兼容入口。' },
  { id: 'azure-openai', wire: 4, label: 'Azure OpenAI', path: '', hint: '填写 Azure 资源根地址；部署名与 API 版本在下方单独设置。' },
  { id: 'ollama', wire: 5, label: 'Ollama', path: '', hint: '填写 Ollama 服务根地址。通常不需要凭据；不会在浏览器直连。' }
] as const

export type ProviderProtocol = typeof providerProtocols[number]['id']
export interface ProviderOptions {
  azureDeployment?: string
  azureApiVersion?: string
  anthropicVersion?: string
}

export const providerPresets: ReadonlyArray<{ id: string; label: string; protocol: ProviderProtocol }> = [
  { id: 'openai', label: 'OpenAI / 自定义兼容服务', protocol: 'openai-chat' },
  { id: 'deepseek', label: 'DeepSeek（OpenAI 兼容）', protocol: 'openai-chat' },
  { id: 'qwen', label: '通义千问（OpenAI 兼容）', protocol: 'openai-chat' },
  { id: 'moonshot', label: 'Moonshot / Kimi（OpenAI 兼容）', protocol: 'openai-chat' },
  { id: 'groq', label: 'Groq（OpenAI 兼容）', protocol: 'openai-chat' },
  { id: 'anthropic', label: 'Anthropic', protocol: 'anthropic-messages' },
  { id: 'gemini', label: 'Google Gemini', protocol: 'gemini-generate-content' },
  { id: 'azure', label: 'Azure OpenAI', protocol: 'azure-openai' },
  { id: 'ollama', label: 'Ollama', protocol: 'ollama' }
]

export function isProviderProtocol(value: unknown): value is ProviderProtocol {
  return providerProtocols.some((item) => item.id === value)
}

export function protocolFromWire(value?: ZbaProviderProtocol): ProviderProtocol {
  return providerProtocols.find((item) => item.wire === (value ?? 0))?.id ?? 'openai-chat'
}

export function protocolToWire(value: string): ZbaProviderProtocol {
  const protocol = providerProtocols.find((item) => item.id === value)
  if (!protocol) throw new Error('Unsupported provider protocol')
  return protocol.wire
}

export function protocolLabel(value?: ZbaProviderProtocol): string {
  return providerProtocols.find((item) => item.wire === (value ?? 0))?.label ?? '不支持的协议'
}

export function endpointError(raw: string): string | null {
  if (!raw.trim()) return '请填写 API 根地址。'
  try {
    const url = new URL(raw)
    if (!['https:', 'http:'].includes(url.protocol)) return '根地址只支持 HTTP 或 HTTPS。'
    if (url.username || url.password || url.search || url.hash) return '根地址不能包含凭据、查询参数或片段；API Key 请单独填写。'
    return null
  } catch {
    return '请填写完整的 API 根地址。'
  }
}
