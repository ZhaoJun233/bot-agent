(function () {
  'use strict';

  let port = null;
  let session = null;
  let seq = 0;
  let loaded = false;
  const pending = new Map();
  const rowControls = new Map();

  const banner = document.getElementById('banner');
  const metaEl = document.getElementById('quota-meta');
  const listEl = document.getElementById('quota-list');
  const recoveryPanel = document.getElementById('recovery-panel');
  const recoveryList = document.getElementById('recovery-list');

  function showBanner(text) {
    if (!text) {
      banner.hidden = true;
      banner.textContent = '';
      return;
    }
    banner.hidden = false;
    banner.textContent = text;
  }

  function rejectPending(reason) {
    for (const [, waiter] of pending) {
      waiter.reject(new Error(reason));
    }
    pending.clear();
  }

  function callBridge(action, payload) {
    if (!port || !session) return Promise.reject(new Error('bridge_not_ready'));
    const requestId = `req-${++seq}`;
    const activePort = port;
    return new Promise((resolve, reject) => {
      pending.set(requestId, { resolve, reject });
      activePort.postMessage({
        type: 'zba.ui.request',
        requestId,
        channelId: session.channelId,
        pluginId: session.pluginId,
        version: session.version,
        releaseSha256: session.releaseSha256,
        generationId: session.generationId,
        action,
        payload: payload || {},
      });
    });
  }

  function canWrite() {
    return Boolean(session && session.mode === 'admin' && session.writesEnabled && session.backendReady && session.versionMatched);
  }

  function stateBannerText() {
    if (!session) return '';
    if (!session.versionMatched) {
      return '前后端插件版本不一致，已禁用写入（仍可按原操作键核对未决操作）。';
    }
    if (!session.backendReady) {
      return '后端插件未就绪，已禁用写入。';
    }
    return '';
  }

  function syncWritableState() {
    const writable = canWrite();
    for (const ctrl of rowControls.values()) {
      if (!ctrl.saving) {
        ctrl.input.disabled = !writable;
        ctrl.saveBtn.disabled = !writable;
      }
    }
    const statusBanner = stateBannerText();
    if (statusBanner) {
      showBanner(statusBanner);
    }
  }

  function renderQuotas(page) {
    listEl.innerHTML = '';
    rowControls.clear();
    const items = Array.isArray(page && page.items) ? page.items : [];
    if (items.length === 0) {
      listEl.textContent = '暂无已登记的会话配额记录。';
      return;
    }
    const writable = canWrite();
    for (const item of items) {
      const minLimit = Number.isInteger(item.minimumDailyTokenLimit) ? item.minimumDailyTokenLimit : 1;
      const maxLimit = Number.isInteger(item.maximumDailyTokenLimit) ? item.maximumDailyTokenLimit : 1000000000;
      const usedTokens = item.usedTokens ?? 0;

      const row = document.createElement('div');
      row.className = 'quota-row';
      const info = document.createElement('div');
      const title = document.createElement('strong');
      title.textContent = item.displayName || item.sourceKey;
      const detail = document.createElement('div');
      detail.className = 'muted';
      detail.textContent = `已用 ${usedTokens} / 限额 ${item.dailyTokenLimit ?? 0} · 修订 ${item.revision ?? '0'}`;
      info.append(title, detail);

      const controls = document.createElement('div');
      controls.className = 'quota-controls';
      const input = document.createElement('input');
      input.type = 'number';
      input.min = String(minLimit);
      input.max = String(maxLimit);
      input.step = '1';
      input.value = String(item.dailyTokenLimit ?? minLimit);
      input.disabled = !writable;

      const saveBtn = document.createElement('button');
      saveBtn.type = 'button';
      saveBtn.className = 'primary';
      saveBtn.textContent = '保存';
      saveBtn.disabled = !writable;

      const ctrlState = { input, saveBtn, saving: false };
      rowControls.set(item.sourceKey, ctrlState);

      saveBtn.addEventListener('click', async () => {
        showBanner('');
        const raw = input.value.trim();
        const limit = Number(raw);
        if (!/^\d+$/.test(raw) || !Number.isSafeInteger(limit) || limit < minLimit || limit > maxLimit) {
          showBanner(`每日配额必须为 ${minLimit} 到 ${maxLimit} 之间的整数。`);
          return;
        }
        ctrlState.saving = true;
        input.disabled = true;
        saveBtn.disabled = true;
        try {
          await callBridge('quotas.save', {
            sourceKey: item.sourceKey,
            dailyTokenLimit: limit,
            expectedRevision: String(item.revision ?? '0'),
          });
          await refresh();
        } catch (err) {
          showBanner((err && err.message) || '保存请求失败，可通过原操作键核对结果。');
        } finally {
          ctrlState.saving = false;
          input.disabled = !canWrite();
          saveBtn.disabled = !canWrite();
          await renderPending();
        }
      });

      controls.append(input, saveBtn);
      row.append(info, controls);
      listEl.append(row);
    }
  }

  async function renderPending() {
    try {
      const ops = await callBridge('operations.list');
      const items = Array.isArray(ops) ? ops : [];
      recoveryList.innerHTML = '';
      recoveryPanel.hidden = items.length === 0;
      for (const op of items) {
        const row = document.createElement('div');
        row.className = 'quota-row';
        const label = document.createElement('span');
        label.textContent = `${op.target || op.key} (${op.state})`;
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.textContent = '按原键核对';
        btn.addEventListener('click', async () => {
          showBanner('');
          try {
            const found = await callBridge('quotas.find', { idempotencyKey: op.key });
            const message = `原键核对结果：${found && found.found ? '已确认提交' : '未查到提交记录'}`;
            await refresh({ preserveBanner: true });
            showBanner(message);
          } catch (err) {
            showBanner((err && err.message) || '核对失败');
          }
        });
        row.append(label, btn);
        recoveryList.append(row);
      }
    } catch {
      /* ignore when bridge unavailable */
    }
  }

  async function refresh(options) {
    const preserveBanner = Boolean(options && options.preserveBanner);
    if (!preserveBanner) {
      showBanner('');
    }
    if (!session) return;
    const statusBanner = stateBannerText();
    if (statusBanner && !preserveBanner) {
      showBanner(statusBanner);
    }
    try {
      const page = await callBridge('quotas.list');
      renderQuotas(page);
      loaded = true;
    } catch (err) {
      listEl.textContent = '无法读取配额列表。';
      if (!preserveBanner) {
        showBanner((err && err.message) || '读取失败');
      }
    }
    await renderPending();
  }

  document.getElementById('refresh-btn').addEventListener('click', () => {
    void refresh();
  });
  document.getElementById('close-btn').addEventListener('click', () => {
    void callBridge('dialog.close');
  });
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') void callBridge('dialog.close');
  });

  window.addEventListener('message', (event) => {
    if (event.source !== window.parent) return;
    const data = event.data;
    if (!data || data.type !== 'zba.ui.init' || !event.ports || !event.ports[0]) return;
    if (port) {
      port.onmessage = null;
      port.close();
      rejectPending('bridge_reinitialized');
    }
    port = event.ports[0];
    session = data;
    if (data.theme) document.documentElement.setAttribute('data-theme', data.theme);
    metaEl.textContent = `v${data.version} · 代次 ${data.generationId} · ${data.adoptionMode === 'cold' ? '重启采用' : '在线采用'}`;
    const currentPort = port;
    currentPort.onmessage = (msgEvent) => {
      if (currentPort !== port) return;
      const msg = msgEvent.data;
      if (!msg) return;
      if (msg.type === 'zba.ui.state') {
        session = Object.assign({}, session, msg);
        if (msg.theme) document.documentElement.setAttribute('data-theme', msg.theme);
        if (loaded) {
          syncWritableState();
        } else {
          void refresh();
        }
        return;
      }
      if (msg.type === 'zba.ui.response' && pending.has(msg.requestId)) {
        const waiter = pending.get(msg.requestId);
        pending.delete(msg.requestId);
        if (msg.ok) waiter.resolve(msg.data);
        else waiter.reject(new Error(msg.error || 'bridge_error'));
      }
    };
    currentPort.postMessage({ type: 'zba.ui.ready', channelId: session.channelId });
    void refresh();
  });
})();
