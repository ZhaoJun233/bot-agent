import type { Component } from 'vue'

export interface UiError { code: string; message: string; status?: number; requestId?: string }
export interface RequestError { status: number; code: string }
export interface UiDialogProps {
  title: string; titleId: string; testId: string; active?: boolean
  trigger?: HTMLElement | null; fallback?: HTMLElement | null; wide?: boolean; drawer?: boolean
  onClose?: () => void
}
export interface UiErrorNoticeProps { error: UiError | null; title?: string }
export interface UiPluginSwitchProps {
  blocked: boolean; runtimeState?: number
  onBusy?: (value: boolean) => void; onSaved?: () => void; onAuthExpired?: (error: UiError) => void
}
export interface UiOperation {
  readonly key: string
  readonly target: string
  readonly meta: Readonly<Record<string, string | number | boolean>>
  readonly state: 'in-flight' | 'unknown' | 'settled'
}

/** Bound by the shell to one authenticated scope and one operation kind. No tokens or bodies. */
export interface UiOperationJournal {
  list(): readonly UiOperation[]
  begin(input: { key: string; target: string; meta?: Readonly<Record<string, string | number | boolean>> }): void
  mark(key: string, state: 'unknown' | 'settled'): void
}

/** D1 in-process presentation services; runtime UI transport and activation are separate D2 work. */
export interface PluginUiBridge {
  readonly components: {
    readonly ManagementDialog: Component<UiDialogProps>
    readonly ErrorNotice: Component<UiErrorNoticeProps>
    readonly PluginSwitch: Component<UiPluginSwitchProps>
  }
  readonly operations: UiOperationJournal
  requestError(cause: unknown): RequestError | null
  toUiError(cause: unknown): UiError
  newOperationKey(): string
  protectDraft(hasUnsavedWork: () => boolean): void
}
