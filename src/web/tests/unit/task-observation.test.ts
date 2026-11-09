import { describe, expect, it } from 'vitest'
import { TaskObservation } from '../../src/api/task-observation'

function checkDenominators(observation: TaskObservation): void {
  const s = observation.snapshot()
  expect(s.attempts).toBe(s.accepted + s.acceptedReplays + s.rejected + s.acceptanceUnknown + s.awaitingAcceptance)
  expect(Object.values(s.states).reduce((sum, count) => sum + count, 0)).toBe(s.accepted)
  expect(s.latency.knownCount + s.latency.unknownCount).toBe(s.accepted)
}

describe('client task observation', () => {
  it('counts before any acknowledgement, then measures a confirmed terminal observation', () => {
    let now = 10
    const observation = new TaskObservation(() => now)
    const token = observation.begin('synthetic-key')
    expect(observation.snapshot()).toMatchObject({ attempts: 1, awaitingAcceptance: 1, acceptedCohortComplete: false })
    observation.accepted(token, 'synthetic-task', 1)
    now = 35
    observation.observe('synthetic-task', 3)
    expect(observation.snapshot()).toMatchObject({ accepted: 1, acceptedReplays: 0, acceptedCohortComplete: true,
      states: { succeeded: 1 }, latency: { knownCount: 1, unknownCount: 0, p50Ms: 25, p95Ms: 25 } })
    checkDenominators(observation)
  })

  it('counts a replay as another attempt and preserves the first terminal latency', () => {
    let now = 0
    const observation = new TaskObservation(() => now)
    observation.accepted(observation.begin('key'), 'task', 1)
    now = 5; observation.observe('task', 3)
    now = 100; observation.accepted(observation.begin('key'), 'task', 3)
    observation.observe('task', 1)
    expect(observation.snapshot()).toMatchObject({ attempts: 2, accepted: 1, acceptedReplays: 1,
      states: { succeeded: 1, inFlight: 0 }, latency: { p95Ms: 5 } })
    checkDenominators(observation)
  })

  it('Find resolves one accepted intent, leaving other uncertain attempts unknown', () => {
    const observation = new TaskObservation(() => 10)
    observation.failed(observation.begin('key'), 'unknown')
    observation.failed(observation.begin('key'), 'unknown')
    observation.found('key', 'task', 3)
    observation.found('key', 'task', 3)
    expect(observation.snapshot()).toMatchObject({ attempts: 2, accepted: 1, acceptedReplays: 0,
      acceptanceUnknown: 1, acceptedCohortComplete: false })
    checkDenominators(observation)
  })

  it('keeps acceptance proved by Find even if the outstanding POST later fails', () => {
    const observation = new TaskObservation(() => 10)
    const token = observation.begin('key')
    observation.found('key', 'task', 1)
    observation.failed(token, 'unknown')
    observation.accepted(token, 'task', 1)
    expect(observation.snapshot()).toMatchObject({ accepted: 1, acceptedReplays: 0, acceptanceUnknown: 0 })
    checkDenominators(observation)
  })

  it('does not count tasks discovered outside the tracked submission cohort', () => {
    const observation = new TaskObservation()
    observation.observe('unrelated-task', 3)
    observation.found('old-key', 'old-task', 3)
    observation.failed(observation.begin('rejected-key'), 'rejected')
    observation.found('rejected-key', 'preexisting-task', 3)
    expect(observation.snapshot()).toMatchObject({ accepted: 0, rejected: 1, samples: [] })
    checkDenominators(observation)
  })

  it('keeps unavailable reads separate from Runtime Unknown and recovers on a valid read', () => {
    const observation = new TaskObservation(() => 0)
    observation.accepted(observation.begin('key'), 'task', 1)
    observation.unavailable('task')
    expect(observation.snapshot().states).toMatchObject({ unreadable: 1, unknown: 0, inFlight: 0 })
    observation.observe('task', 7)
    expect(observation.snapshot()).toMatchObject({ states: { unreadable: 0, unknown: 1 },
      latency: { knownCount: 0, unknownCount: 1, p50Ms: null, p95Ms: null } })
    observation.unavailable('task')
    checkDenominators(observation)
  })

  it('bounds storage and makes untracked attempts explicit without dropping earlier records', () => {
    const observation = new TaskObservation(() => 0, 2)
    observation.accepted(observation.begin('key-1'), 'task-1', 3)
    observation.failed(observation.begin('key-2'), 'rejected')
    observation.accepted(observation.begin('key-3'), 'task-3', 3)
    expect(observation.snapshot()).toMatchObject({ attempts: 2, untrackedAttempts: 1, accepted: 1,
      completeAttempts: false, acceptedCohortComplete: false })
    checkDenominators(observation)
  })

  it('isolates instances and exports only aliases, fixed fields, states and measurements', () => {
    const first = new TaskObservation(() => 0)
    const second = new TaskObservation(() => 0)
    first.accepted(first.begin('private-key-synthetic'), 'private-id-synthetic', 0)
    second.found('private-key-synthetic', 'private-id-synthetic', 3)
    const exported = JSON.stringify(first.snapshot())
    expect(exported).not.toContain('private-key-synthetic')
    expect(exported).not.toContain('private-id-synthetic')
    expect(first.snapshot().scopeId).not.toBe(second.snapshot().scopeId)
    expect(second.snapshot().accepted).toBe(0)
    expect(first.snapshot()).toMatchObject({ taskUsage: null, attributedCost: null, actualUsefulness: null, humanMinutes: null })
  })

  it('marks inconsistent identity and unknown state as incomplete observation', () => {
    const observation = new TaskObservation(() => 0)
    observation.accepted(observation.begin('key'), 'task-1', 0)
    observation.accepted(observation.begin('key'), 'task-2', 0)
    observation.observe('task-1', 99)
    expect(observation.snapshot()).toMatchObject({ identityConflicts: 1, observationFaults: 1,
      completeAttempts: false, accepted: 1, acceptedReplays: 0, acceptanceUnknown: 1 })
    checkDenominators(observation)
  })

  it('never substitutes zero for an invalid clock measurement', () => {
    let now = 10
    const observation = new TaskObservation(() => now)
    observation.accepted(observation.begin('key'), 'task', 0)
    now = 5; observation.observe('task', 3)
    expect(observation.snapshot().latency).toMatchObject({ knownCount: 0, unknownCount: 1, p50Ms: null, p95Ms: null })
    checkDenominators(observation)
  })

  it('does not count different intents returning the same task as verified replay', () => {
    const observation = new TaskObservation(() => 10)
    observation.accepted(observation.begin('key-1'), 'task', 1)
    observation.failed(observation.begin('key-2'), 'unknown')
    observation.found('key-2', 'task', 3)
    expect(observation.snapshot()).toMatchObject({ accepted: 1, acceptedReplays: 0,
      acceptanceUnknown: 1, identityConflicts: 1, completeAttempts: false, states: { inFlight: 1 } })
    checkDenominators(observation)
  })
})
