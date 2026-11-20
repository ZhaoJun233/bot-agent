(function () {
  'use strict';

  const badge = document.getElementById('generation-badge');
  const status = document.getElementById('status-line');
  const closeBtn = document.getElementById('close-btn');

  let port = null;
  let session = null;
  let seq = 0;

  function renderState() {
    if (!session) return;
    if (session.theme) {
      document.documentElement.setAttribute('data-theme', session.theme);
    }
    if (badge) {
      badge.textContent = `v${session.version} · 代次 ${session.generationId}`;
    }
    const readOnly = session.mode !== 'admin' || !session.writesEnabled || !session.backendReady || !session.versionMatched;
    if (status) {
      if (!session.versionMatched) {
        status.textContent = '前后端代际或版本不匹配，当前仅只读展示策略说明。';
      } else if (!session.backendReady) {
        status.textContent = '后端插件未就绪，当前仅只读展示策略说明。';
      } else {
        status.textContent = readOnly
          ? '只读会话已建立：仅展示安全策略与代际信息。'
          : '活跃代际会话已建立（策略由独立包代际托管）。';
      }
    }
  }

  function requestClose() {
    if (!port || !session) return;
    port.postMessage({
      type: 'zba.ui.request',
      requestId: `req-${++seq}`,
      channelId: session.channelId,
      pluginId: session.pluginId,
      version: session.version,
      releaseSha256: session.releaseSha256,
      generationId: session.generationId,
      action: 'dialog.close',
      payload: {},
    });
  }

  if (closeBtn) {
    closeBtn.addEventListener('click', requestClose);
  }
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') requestClose();
  });

  window.addEventListener('message', (event) => {
    if (event.source !== window.parent) return;
    const msg = event.data;
    if (!msg || typeof msg !== 'object' || msg.type !== 'zba.ui.init' || !event.ports || !event.ports[0]) return;
    if (port) {
      port.onmessage = null;
      port.close();
    }
    port = event.ports[0];
    session = msg;
    renderState();
    const currentPort = port;
    currentPort.onmessage = (msgEvent) => {
      if (currentPort !== port) return;
      const update = msgEvent.data;
      if (!update || typeof update !== 'object') return;
      if (update.type === 'zba.ui.state') {
        session = Object.assign({}, session, update);
        renderState();
      }
    };
    currentPort.postMessage({ type: 'zba.ui.ready', channelId: session.channelId });
  });
})();
