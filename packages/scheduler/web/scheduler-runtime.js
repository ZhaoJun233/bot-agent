(() => {
  const statusEl = document.getElementById('status-line')
  const listEl = document.getElementById('jobs-list')
  let port = null
  let initData = null
  const pending = new Map()
  let reqSeq = 0

  function applyTheme(theme) {
    if (typeof theme === 'string' && theme) {
      document.documentElement.setAttribute('data-theme', theme)
    }
  }

  function rejectAllPending(code) {
    for (const entry of pending.values()) {
      entry.reject(new Error(code))
    }
    pending.clear()
  }

  function call(action, payload) {
    if (!port || !initData) return Promise.reject(new Error('bridge_not_ready'))
    const requestId = `sched-${++reqSeq}`
    return new Promise((resolve, reject) => {
      pending.set(requestId, { resolve, reject })
      port.postMessage({
        type: 'zba.ui.request',
        requestId,
        channelId: initData.channelId,
        pluginId: initData.pluginId,
        version: initData.version,
        releaseSha256: initData.releaseSha256,
        generationId: initData.generationId,
        action,
        payload: payload || {}
      })
    })
  }

  function renderJobs(data) {
    const jobs = (data && Array.isArray(data.jobs)) ? data.jobs : []
    if (!jobs.length) {
      listEl.textContent = '暂无定时任务。'
      return
    }
    listEl.textContent = ''
    for (const j of jobs) {
      const row = document.createElement('div')
      row.textContent = `${j.jobId} · ${j.kind} · ${j.timezoneIana} · ${j.enabled ? '启用' : '暂停'}`
      listEl.appendChild(row)
    }
  }

  async function reload() {
    try {
      const res = await call('scheduler.list')
      renderJobs(res)
      statusEl.textContent = `已加载（版本 rev ${res.revision ?? 0}）`
    } catch (err) {
      statusEl.textContent = `加载失败：${err && err.message ? err.message : 'unknown'}`
    }
  }

  document.getElementById('refresh-btn').addEventListener('click', () => { void reload() })
  document.getElementById('close-btn').addEventListener('click', () => { void call('dialog.close') })
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') void call('dialog.close')
  })

  window.addEventListener('message', (event) => {
    if (event.source !== window.parent) return
    const msg = event.data
    if (!msg || typeof msg !== 'object' || msg.type !== 'zba.ui.init' || !event.ports || !event.ports[0]) return
    if (port) {
      port.onmessage = null
      port.close()
    }
    rejectAllPending('bridge_reinitialized')
    initData = msg
    applyTheme(msg.theme)
    const activePort = event.ports[0]
    port = activePort
    activePort.onmessage = (ev) => {
      if (activePort !== port) return
      const res = ev.data
      if (!res || typeof res !== 'object') return
      if (res.type === 'zba.ui.state') {
        initData = Object.assign({}, initData, res)
        applyTheme(initData.theme)
        return
      }
      if (!res.requestId) return
      const entry = pending.get(res.requestId)
      if (!entry) return
      pending.delete(res.requestId)
      if (res.ok) entry.resolve(res.data)
      else entry.reject(new Error(res.error || 'request_failed'))
    }
    void reload()
  })
})()
