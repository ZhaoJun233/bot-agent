import { describe, expect, it, vi } from 'vitest'
import { ZbaClient, ZbaNetworkError } from '../../src/api/zba'
import { TaskObservation } from '../../src/api/task-observation'

const request = { sourceKey: 'group:10001', sessionId: 'synthetic-session', prompt: 'synthetic body', target: 'server' as const, timeoutSeconds: 30 }
const accepted = { id: 'synthetic-task', state: 0, acceptedAt: '2026-11-09T00:00:00Z' }
const task = { id: accepted.id, state: 3, target: 'server', sessionId: request.sessionId,
  createdAt: accepted.acceptedAt, updatedAt: accepted.acceptedAt, cancellationRequested: false }
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('ZbaClient task observation integration', () => {
  it('still constructs and submits where secure-context randomUUID is unavailable', async () => {
    vi.stubGlobal('crypto', { getRandomValues: crypto.getRandomValues.bind(crypto) })
    try {
      const client = new ZbaClient({ fetchImpl: async () => response(accepted, 202) })
      await expect(client.submitTask(request, 'key')).resolves.toEqual(accepted)
      expect(client.getTaskObservation()).toMatchObject({ attempts: 1, accepted: 1, completeAttempts: true })
    } finally { vi.unstubAllGlobals() }
  })

  it('keeps the task API usable when an observation scope cannot be generated', async () => {
    vi.stubGlobal('crypto', { getRandomValues: () => { throw new Error('synthetic entropy unavailable') } })
    try {
      const client = new ZbaClient({ fetchImpl: async () => response(accepted, 202) })
      await expect(client.submitTask(request, 'key')).resolves.toEqual(accepted)
      expect(client.getTaskObservation()).toMatchObject({ scopeId: null, accepted: 1, completeAttempts: false, observationFaults: 1 })
    } finally { vi.unstubAllGlobals() }
  })

  it('records the attempt before fetch and counts a validated replay once per task', async () => {
    const fetchImpl: typeof fetch = async () => {
      expect(client.getTaskObservation().awaitingAcceptance).toBe(1)
      return response(accepted, 202)
    }
    const client = new ZbaClient({ token: 'synthetic-token', fetchImpl })
    await expect(client.submitTask(request, 'key')).resolves.toEqual(accepted)
    await client.submitTask(request, 'key')
    expect(client.getTaskObservation()).toMatchObject({ attempts: 2, accepted: 1, acceptedReplays: 1 })
  })

  it.each([400, 401, 403, 404, 422])('retains a definite %s refusal', async status => {
    const client = new ZbaClient({ fetchImpl: async () => response({ error: { code: 'synthetic_rejection', message: 'synthetic' } }, status) })
    await expect(client.submitTask(request, 'key')).rejects.toMatchObject({ status })
    expect(client.getTaskObservation()).toMatchObject({ attempts: 1, rejected: 1, accepted: 0, acceptanceUnknown: 0 })
  })

  it.each([408, 409, 429, 500, 503])('keeps ambiguous %s admission unknown', async status => {
    const client = new ZbaClient({ fetchImpl: async () => response(accepted, status) })
    await expect(client.submitTask(request, 'key')).rejects.toMatchObject({ status })
    expect(client.getTaskObservation()).toMatchObject({ accepted: 0, rejected: 0, acceptanceUnknown: 1 })
  })

  it('retains a dropped acknowledgement until a validated Find confirms acceptance', async () => {
    const fetchImpl = vi.fn<typeof fetch>()
      .mockRejectedValueOnce(new Error('synthetic reset'))
      .mockResolvedValueOnce(response({ code: 'unknown', instanceId: 'synthetic-instance' }))
      .mockResolvedValueOnce(response({ code: 'found', instanceId: 'synthetic-instance', task }))
    const client = new ZbaClient({ fetchImpl })
    await expect(client.submitTask(request, 'private-key')).rejects.toBeInstanceOf(ZbaNetworkError)
    await client.findTask('private-key')
    expect(client.getTaskObservation()).toMatchObject({ accepted: 0, acceptanceUnknown: 1 })
    await client.findTask('private-key')
    const snapshot = client.getTaskObservation()
    expect(snapshot).toMatchObject({ accepted: 1, acceptanceUnknown: 0, states: { succeeded: 1 } })
    const serialized = JSON.stringify(snapshot)
    for (const secret of ['private-key', request.sourceKey, request.sessionId, request.prompt, task.id]) expect(serialized).not.toContain(secret)
  })

  it('does not treat malformed successful responses as acceptance', async () => {
    const client = new ZbaClient({ fetchImpl: async () => response({ ...accepted, id: '' }, 202) })
    await expect(client.submitTask(request, 'key')).rejects.toMatchObject({ code: 'invalid_json' })
    expect(client.getTaskObservation()).toMatchObject({ accepted: 0, acceptanceUnknown: 1 })
  })

  it('task list reads update only previously accepted tasks', async () => {
    const fetchImpl = vi.fn<typeof fetch>().mockResolvedValueOnce(response(accepted, 202))
      .mockResolvedValueOnce(response({ items: [task, { ...task, id: 'unrelated-task' }] }))
    const client = new ZbaClient({ fetchImpl })
    await client.submitTask(request, 'key')
    await client.listTasks()
    expect(client.getTaskObservation()).toMatchObject({ accepted: 1, states: { succeeded: 1 } })
  })

  it('does not let observer failure change the task return value', async () => {
    vi.spyOn(TaskObservation.prototype, 'accepted').mockImplementation(() => { throw new Error('synthetic measurement failure') })
    const client = new ZbaClient({ fetchImpl: async () => response(accepted, 202) })
    await expect(client.submitTask(request, 'key')).resolves.toEqual(accepted)
    expect(client.getTaskObservation()).toMatchObject({ observationFaults: 1, completeAttempts: false })
  })
})
