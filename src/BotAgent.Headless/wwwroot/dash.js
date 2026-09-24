/**
 * 健康仪表盘（通用 Agent 平台 · 批次 J）：把已经存在的东西**摊在一屏**。
 *
 * 数据来自 GET /api/dashboard（服务端自己不去做新统计，只汇总）：
 *   运行与连接 / 会话与队列 / 模型延迟 / 宿主内存与负载 / 工具目录 / 会话权限 / 轨迹容量。
 *
 * 三条纪律（与追踪页同一套）：
 *   · **纯只读**：这一页不向服务端写任何东西；
 *   · **不引图表库**：数字 + 徽标就够（文档明确要求，别把它做成监控大屏）；
 *   · **一律文本节点**：服务端来的字符串只用 createElement + textContent。
 */
(() => {
  "use strict";

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null && text !== "") node.textContent = String(text);
    return node;
  }

  function bytes(value) {
    const n = Number(value);
    if (!isFinite(n) || n <= 0) return "—";
    const mb = n / 1024 / 1024;
    return mb >= 1024 ? (mb / 1024).toFixed(2) + " GB" : mb.toFixed(0) + " MB";
  }

  function seconds(value) {
    const n = Number(value) || 0;
    const h = Math.floor(n / 3600);
    const m = Math.floor((n % 3600) / 60);
    return h > 0 ? h + " 小时 " + m + " 分" : m + " 分";
  }

  /// 一张数字卡：标题 + 大字 + 一句说明（徽标用文字，不用图标字体）。
  function tile(label, big, hint, tone) {
    const box = el("div", "card dash-tile");
    box.appendChild(el("div", "dash-label", label));
    box.appendChild(el("div", "dash-big" + (tone ? " " + tone : ""), big));
    if (hint) box.appendChild(el("div", "dash-hint", hint));
    return box;
  }

  function render(data) {
    const host = document.getElementById("dashGrid");
    if (!host) return;
    host.replaceChildren();

    if (!data) {
      host.appendChild(el("p", "trace-empty", "还没拿到数据，点右上角刷新。"));
      return;
    }

    const memory = data.memory || {};
    const ratio = Number(memory.limitBytes) > 0 && Number(memory.usedBytes) > 0
      ? Math.min(100, Math.round((Number(memory.usedBytes) / Number(memory.limitBytes)) * 100))
      : null;
    const tools = data.tools || {};
    const policy = data.sessionPolicy || {};
    const traces = data.traces || {};

    host.appendChild(tile("运行", seconds(data.uptimeSeconds), "已运行"));
    host.appendChild(tile("AI 总开关", data.aiMode ? "开" : "关", data.aiMode ? "会自动回复" : "全部忽略",
      data.aiMode ? "ok" : "mute"));
    host.appendChild(tile("协议端", data.onebot ? "已连接" : "断开",
      data.accountOnline === true ? "账号在线" : data.accountOnline === false ? "账号掉线" : "在线状态未知",
      data.onebot && data.accountOnline !== false ? "ok" : "bad"));
    host.appendChild(tile("会话", String(data.conversations || 0),
      "在途 " + (data.inFlight || 0) + " · 排队 " + (data.queued || 0)));
    host.appendChild(tile("模型延迟", (data.latencyMs || 0) > 0 ? (data.latencyMs + "ms") : "—", "最近一轮生成"));
    host.appendChild(tile("内存", bytes(memory.usedBytes),
      ratio === null ? "容器上限未知" : "上限 " + bytes(memory.limitBytes) + "（用了 " + ratio + "%）",
      ratio !== null && ratio >= 85 ? "warn" : null));
    host.appendChild(tile("负载", data.load === null || data.load === undefined ? "—" : String(data.load), "1 分钟均值"));
    host.appendChild(tile("工具目录", String(tools.total || 0),
      "高风险 " + (tools.highRisk || 0) + " · 需批准 " + (tools.needApproval || 0)
      + " · 执行者 " + (tools.executors || 0) + (tools.healthy ? "" : " · ⚠ 自检没过"),
      tools.healthy ? null : "bad"));
    host.appendChild(tile("会话权限", policy.available ? String(policy.sessions || 0) + " 条" : "—",
      policy.available
        ? ("陈旧 " + (policy.stale || 0) + " · 重建过 " + (policy.rebuilt || 0))
        : "这一版没接上"));
    host.appendChild(tile("轨迹", traces.available ? String(traces.recent || 0) + " 轮" : "—",
    traces.available ? ("进行中 " + (traces.active || 0) + " · 最多留 " + (traces.capacity || 0)) : "这一版没接上"));
  }

  window.DashPage = { render: render };
})();
