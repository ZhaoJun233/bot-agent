(() => {
  const statusEl = document.getElementById('status-line')
  const listEl = document.getElementById('todo-list')
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
    const requestId = `todo-${++reqSeq}`
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

  function renderTodos(data) {
    const items = (data && Array.isArray(data.items)) ? data.items : []
    if (!items.length) {
      listEl.textContent = '暂无个人待办。'
      return
    }
    listEl.textContent = ''
    for (const item of items) {
      const row = document.createElement('div')
      row.textContent = `[P${item.priority}] ${item.title} (${item.status})`
      listEl.appendChild(row)
    }
  }

  async function reload() {
    if (!initData || initData.mode !== 'admin') {
      listEl.textContent = '切换管理员模式后可查看个人待办。'
      statusEl.textContent = '只读模式（个人待办仅限管理员可见）'
      return
    }
    try {
      const res = await call('todo.list')
      if (!initData || initData.mode !== 'admin') return
      renderTodos(res)
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
        const prevMode = initData ? initData.mode : 'read'
        initData = Object.assign({}, initData, res)
        applyTheme(initData.theme)
        if (initData.mode !== 'admin') {
          listEl.textContent = '切换管理员模式后可查看个人待办。'
          statusEl.textContent = '只读模式（个人待办仅限管理员可见）'
        } else if (prevMode !== 'admin') {
          void reload()
        }
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
