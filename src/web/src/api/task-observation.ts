import type { ZbaTaskState } from './zba'
import { newNonce } from '../shared/nonce'

type AttemptPhase = 'awaiting' | 'accepted' | 'rejected' | 'unknown'
interface Intent { key: string; firstStartedMs: number; taskId?: string }
interface Attempt { intent: Intent; phase: AttemptPhase }
interface ObservedTask {
  intent: Intent
  alias: string
  firstStartedMs: number
  state: ZbaTaskState | null
  unavailable: boolean
  terminalElapsedMs: number | null
}

const terminal = (state: number | null) => state === 3 || state === 4 || state === 6 || state === 7
const quantile = (values: number[], q: number): number | null =>
  values.length ? values[Math.ceil(values.length * q) - 1] ?? null : null

function observationScope(): string | null {
  try { return 'client-' + newNonce() }
  catch { return null } // An unavailable measurement must never block connection or submission.
}

/** One client/identity lifetime. Keys and task ids are private, bounded, and never exported. */
export class TaskObservation {
  private readonly attempts: Attempt[] = []
  private readonly intents = new Map<string, Intent>()
  private readonly tasks = new Map<string, ObservedTask>()
  private readonly startedAt = new Date().toISOString()
  private readonly scopeId = observationScope()
  private untrackedAttempts = 0
  private observationFaults = this.scopeId === null ? 1 : 0
  private identityConflicts = 0

  constructor(private readonly now: () => number = () => performance.now(),
              private readonly attemptLimit = 512) {
    if (!Number.isSafeInteger(attemptLimit) || attemptLimit < 1) throw new RangeError('invalid attempt limit')
  }

  begin(key: string): number | null {
    if (this.attempts.length >= this.attemptLimit) {
      this.untrackedAttempts++
      return null
    }
    let intent = this.intents.get(key)
    if (!intent) {
      intent = { key, firstStartedMs: this.now() }
      this.intents.set(key, intent)
    }
    this.attempts.push({ intent, phase: 'awaiting' })
    return this.attempts.length - 1
  }

  accepted(token: number | null, id: string, state: number): void {
    const attempt = token === null ? undefined : this.attempts[token]
    if (!attempt || (attempt.phase !== 'awaiting' && attempt.phase !== 'accepted')) return
    const bound = this.bind(attempt.intent, id, state)
    if (attempt.phase === 'awaiting') attempt.phase = bound ? 'accepted' : 'unknown'
  }

  failed(token: number | null, phase: 'rejected' | 'unknown'): void {
    const attempt = token === null ? undefined : this.attempts[token]
    if (attempt?.phase === 'awaiting') attempt.phase = phase
  }

  found(key: string, id: string, state: number): void {
    const intent = this.intents.get(key)
    if (!intent) return // Find can refer to an intent from before this observation.
    if (!intent.taskId) {
      // Find proves one accepted intent, not that every uncertain retry arrived.
      const uncertain = this.attempts.find(item => item.intent === intent && item.phase === 'unknown')
        ?? this.attempts.find(item => item.intent === intent && item.phase === 'awaiting')
      if (!uncertain) return
      if (this.bind(intent, id, state)) uncertain.phase = 'accepted'
      return
    }
    this.bind(intent, id, state)
  }

  private bind(intent: Intent, id: string, state: number): boolean {
    const existing = this.tasks.get(id)
    if ((intent.taskId && intent.taskId !== id) || (existing && existing.intent !== intent)) {
      this.identityConflicts++
      return false
    }
    intent.taskId = id
    if (!this.tasks.has(id)) {
      this.tasks.set(id, { intent, alias: 'task-' + (this.tasks.size + 1), firstStartedMs: intent.firstStartedMs,
        state: null, unavailable: false, terminalElapsedMs: null })
    }
    this.observe(id, state)
    return true
  }

  observe(id: string, state: number): void {
    const task = this.tasks.get(id)
    if (!task) return // Reading a list never creates an accepted denominator.
    if (!Number.isInteger(state) || state < 0 || state > 7) {
      this.observationFaults++
      return
    }
    if (terminal(task.state)) return // Preserve the first terminal observation against stale reads.
    task.state = state as ZbaTaskState
    task.unavailable = false
    if (terminal(state) && state !== 7) {
      const elapsed = this.now() - task.firstStartedMs
      task.terminalElapsedMs = Number.isFinite(elapsed) && elapsed >= 0 ? elapsed : null
    }
  }

  unavailable(id: string): void {
    const task = this.tasks.get(id)
    if (task && !terminal(task.state)) task.unavailable = true
  }

  markFault(): void { this.observationFaults++ }

  snapshot() {
    const phases = { awaiting: 0, accepted: 0, rejected: 0, unknown: 0 }
    for (const attempt of this.attempts) phases[attempt.phase]++
    const states = { succeeded: 0, failed: 0, cancelled: 0, unknown: 0, inFlight: 0, unreadable: 0 }
    const latencies: number[] = []
    const samples = [...this.tasks.values()].map(task => {
      if (task.unavailable || task.state === null) states.unreadable++
      else if (task.state === 3) states.succeeded++
      else if (task.state === 4) states.failed++
      else if (task.state === 6) states.cancelled++
      else if (task.state === 7) states.unknown++
      else states.inFlight++
      if (task.terminalElapsedMs !== null) latencies.push(task.terminalElapsedMs)
      return { alias: task.alias, lastObservedState: task.state, unavailable: task.unavailable,
        firstAttemptToTerminalObservationMs: task.terminalElapsedMs }
    })
    latencies.sort((a, b) => a - b)
    const completeAttempts = this.untrackedAttempts === 0 && this.observationFaults === 0 && this.identityConflicts === 0
    return {
      schemaVersion: 1, scope: 'client-instance', scopeId: this.scopeId, startedAt: this.startedAt,
      capturedAt: new Date().toISOString(), survivesRefresh: false, attemptLimit: this.attemptLimit,
      attempts: this.attempts.length, untrackedAttempts: this.untrackedAttempts,
      awaitingAcceptance: phases.awaiting, rejected: phases.rejected, acceptanceUnknown: phases.unknown,
      accepted: this.tasks.size, acceptedReplays: phases.accepted - this.tasks.size,
      completeAttempts, acceptedCohortComplete: completeAttempts && phases.awaiting === 0 && phases.unknown === 0,
      observationFaults: this.observationFaults, identityConflicts: this.identityConflicts, states,
      latency: { clock: 'performance.now', kind: 'first-client-attempt-to-terminal-observation',
        knownCount: latencies.length, unknownCount: this.tasks.size - latencies.length,
        p50Ms: quantile(latencies, 0.5), p95Ms: quantile(latencies, 0.95) },
      taskUsage: null, attributedCost: null, actualUsefulness: null, humanMinutes: null, samples
    }
  }
}

export type TaskObservationSnapshot = ReturnType<TaskObservation['snapshot']>
