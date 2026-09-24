/**
 * 追踪页（通用 Agent 平台 · 批次 H）：把「一轮 = 六个节点」演成卡片时间轴。
 *
 * 三条纪律（对应 general-agent-platform-plan.md §7.3 的字段与渲染红线）：
 *   · **纯只读**：这一页不向服务端写任何东西（数据来自 GET /api/traces）；
 *   · **只演形状**：卡片里是工具名 / 状态码 / 耗时 / 计数 —— 群聊正文**不在**这里（正文只在会话气泡里）；
 *   · **一律文本节点**：服务端来的字符串（工具名、原因码、脱敏后的 key）只用 createElement + textContent，
 *     绝不拼进 innerHTML（探针里有一条专门钉这个）。
 *
 * 布局照 pi-web 的三栏思路：左（有轨迹的会话）/ 中（选中那一轮的六张卡）/ 右（本轮详情 + 最近若干轮）。
 */
(() => {
  "use strict";

  // 六节点：顺序即时间轴顺序，也是架构图（§4 的四步）的活动版本。
  const NODES = [
    { kind: "Participation", label: "① 参与判断", hint: "这一轮要不要接话" },
    { kind: "Context", label: "② 上下文截取", hint: "窗口 / 画像 / 气氛（只报条数）" },
    { kind: "Model", label: "③ 模型决策", hint: "说话还是用工具" },
    { kind: "Gate", label: "④ 工具闸门", hint: "登记表 + 策略快照" },
    { kind: "ToolExec", label: "⑤ 工具执行", hint: "真的去干（只报形状）" },
    { kind: "Outbound", label: "⑥ 净化与发送", hint: "清洗 → 分句发出" }
  ];

  // 颜色语义固定：绿=放行 / 灰=静默 / 黄=待审批 / 红=拒绝、失败。
  const STATUS_CLASS = {
    ok: "ok", allowed: "ok", sent: "ok", done: "ok",
    silent: "mute",
    approval_required: "warn", pending: "warn",
    denied: "bad", blocked: "bad", failed: "bad", error: "bad"
  };
  const STATUS_TEXT = {
    ok: "正常", allowed: "放行", sent: "已发出", done: "完成",
    silent: "沉默",
    approval_required: "待审批", pending: "待审批",
    denied: "拒绝", blocked: "拦截", failed: "失败", error: "出错"
  };

  let runs = [];
  let selectedKey = "";
  let selectedRunId = "";
  let approvals = null;

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null && text !== "") node.textContent = String(text);
    return node;
  }

  function statusClass(status) {
    return STATUS_CLASS[status] || "mute";
  }

  function statusText(status) {
    return STATUS_TEXT[status] || (status || "未知");
  }

  function ms(value) {
    const n = Number(value) || 0;
    return n >= 1000 ? (n / 1000).toFixed(1) + "s" : n + "ms";
  }

  /// 一个节点的单行描述：状态 + 耗时 + 工具名 + 原因码 + 计数（**没有参数值、没有正文**）。
  function nodeLine(node) {
    const parts = [statusText(node.status), ms(node.ms)];
    if (node.tool) parts.push(node.tool);
    if (node.reason) parts.push(node.reason);
    if (typeof node.count === "number") parts.push("计数 " + node.count);
    return parts.join(" · ");
  }

  function card(spec, nodes) {
    const box = el("section", "card trace-card");
    const head = el("div", "trace-card-head");
    head.appendChild(el("span", "trace-card-title", spec.label));
    head.appendChild(el("span", "trace-card-hint", spec.hint));
    const badge = nodes.length === 0 ? el("span", "trace-badge mute", "未发生")
      : el("span", "trace-badge " + statusClass(nodes[nodes.length - 1].status), statusText(nodes[nodes.length - 1].status));
    head.appendChild(badge);
    box.appendChild(head);

    if (nodes.length === 0) {
      box.appendChild(el("p", "trace-node mute", "这一轮没走到这一步。"));
      return box;
    }

    for (const node of nodes) {
      const line = el("p", "trace-node " + statusClass(node.status));
      line.appendChild(el("span", "trace-dot"));
      line.appendChild(el("span", "trace-node-text", nodeLine(node)));
      box.appendChild(line);
    }

    return box;
  }

  function drawSummary(payload) {
    const host = document.getElementById("traceSummary");
    if (!host) return;
    if (!payload || payload.available !== true) {
      host.textContent = "轨迹不可用（这一版没接上）";
      return;
    }
    host.textContent = "最近 " + runs.length + " 轮 · 进行中 " + (payload.active || 0)
      + " · 最多留 " + (payload.capacity || 0) + " 轮 · 只有形状、没有正文";
  }

  function drawKeys(keys) {
    const host = document.getElementById("traceKeys");
    if (!host) return;
    host.replaceChildren();
    host.appendChild(el("h2", "trace-col-head", "有轨迹的会话"));

    const all = el("button", "trace-key" + (selectedKey === "" ? " active" : ""), "全部（" + runs.length + " 轮）");
    all.type = "button";
    all.addEventListener("click", () => { selectedKey = ""; selectedRunId = ""; render({ available: true, traces: runs, capacity: lastCapacity, active: lastActive }); });
    host.appendChild(all);

    if (keys.length === 0) {
      host.appendChild(el("p", "trace-empty", "还没有轨迹。"));
      return;
    }

    for (const key of keys) {
      const count = runs.filter((r) => r.key === key).length;
      const btn = el("button", "trace-key" + (selectedKey === key ? " active" : ""), key + "（" + count + " 轮）");
      btn.type = "button";
      btn.addEventListener("click", () => { selectedKey = key; selectedRunId = ""; render({ available: true, traces: runs, capacity: lastCapacity, active: lastActive }); });
      host.appendChild(btn);
    }
  }

  function drawTimeline(visible) {
    const host = document.getElementById("traceRuns");
    if (!host) return;
    host.replaceChildren();

    const run = visible.filter((r) => r.runId === selectedRunId)[0];
    if (!run) {
      host.appendChild(el("p", "trace-empty", "还没有轨迹：等机器人在群里回一轮之后再来刷新。"));
      return;
    }

    const head = el("div", "trace-run-head");
    head.appendChild(el("span", "trace-run-key", run.key));
    head.appendChild(el("span", "trace-run-meta", run.runId + " · " + (run.channel || "") + " · 共 " + ms(run.totalMs)
      + " · " + (run.nodes || []).length + " 个节点"));
    host.appendChild(head);

    for (const spec of NODES) {
      host.appendChild(card(spec, (run.nodes || []).filter((n) => n.kind === spec.kind)));
    }
  }

  function drawDetail(visible) {
    const host = document.getElementById("traceDetail");
    if (!host) return;
    host.replaceChildren();
    drawApprovals(host);
    host.appendChild(el("h2", "trace-col-head", "本轮详情"));

    const run = visible.filter((r) => r.runId === selectedRunId)[0];
    if (run) {
      const rows = [
        ["会话", run.key],
        ["通道", run.channel || "-"],
        ["runId", run.runId],
        ["结论", run.outcome || "-"],
        ["总耗时", ms(run.totalMs)],
        ["节点数", String((run.nodes || []).length)]
      ];
      const table = el("dl", "trace-facts");
      for (const row of rows) {
        table.appendChild(el("dt", null, row[0]));
        table.appendChild(el("dd", null, row[1]));
      }
      host.appendChild(table);
    }

    host.appendChild(el("h2", "trace-col-head", "最近 " + visible.length + " 轮"));
    for (const item of visible.slice(0, 20)) {
      const btn = el("button", "trace-run" + (item.runId === selectedRunId ? " active" : ""),
        clock(item.startedAt) + " · " + (item.outcome || "?") + " · " + ms(item.totalMs));
      btn.type = "button";
      btn.addEventListener("click", () => { selectedRunId = item.runId; render({ available: true, traces: runs, capacity: lastCapacity, active: lastActive }); });
      host.appendChild(btn);
    }

    host.appendChild(el("p", "trace-legend", "绿=放行 / 灰=静默 / 黄=待审批 / 红=拒绝。颜色只表达状态，不表达内容好坏。"));
  }

  function clock(unixMs) {
    const n = Number(unixMs) || 0;
    if (n <= 0) return "-";
    const d = new Date(n);
    const pad = (v) => (v < 10 ? "0" + v : String(v));
    return pad(d.getHours()) + ":" + pad(d.getMinutes()) + ":" + pad(d.getSeconds());
  }

  let lastCapacity = 0;
  let lastActive = 0;

  /// 面板审批（批次 I）：**写路径**。前置条件由服务端把关（审批开关 + 面板令牌），
  /// 这里只负责把「批准 / 拒绝」发出去，然后把结果如实写在一行状态里（成功失败都不装）。
  async function decide(id, approve) {
    if (typeof confirm === "function"
      && !confirm((approve ? "批准" : "拒绝") + "审批单 " + id + "？"
        + (approve ? "\n（批准后服务端会执行那个没有真实副作用的固定假工具）" : ""))) {
      return;
    }

    const note = document.getElementById("traceSummary");
    try {
      const result = await window.PanelApi.post("/api/approvals/decide", { id: id, approve: !!approve });
      if (note) {
        note.textContent = "审批 " + id + " → " + ((result && result.reason) || "已提交");
      }
    } catch (e) {
      if (note) note.textContent = "审批失败：" + (e && e.message ? e.message : String(e));
      return;
    }

    // 结果落地后刷新一屏（轨迹里会多出闸门/执行两个节点）
    try {
      const [traces, list] = await Promise.all([
        window.PanelApi.get("/api/traces?limit=20"),
        window.PanelApi.get("/api/approvals")
      ]);
      render(traces, list);
    } catch { /* 刷新失败不影响“已经批过”这个事实 */ }
  }

  /// 待审批卡：只有**审批开着**时才出现；**未配面板令牌**时明确写出原因（fail-closed，服务端也拒）。
  function drawApprovals(host) {
    if (!approvals || approvals.available !== true || approvals.enabled !== true) {
      return;
    }

    const pending = approvals.pending || [];
    const box = el("section", "card trace-approvals");
    box.appendChild(el("h2", "trace-col-head", "待审批（" + pending.length + "）"));

    if (approvals.canDecide !== true) {
      box.appendChild(el("p", "trace-empty",
        approvals.tokenConfigured === true
          ? "面板审批当前不可用。"
          : "未配置面板令牌 → 面板审批不可用（fail-closed：未配令牌时面板对回环是全开的，这条高权限路径不能被顺带放开）。"));
      host.appendChild(box);
      return;
    }

    if (pending.length === 0) {
      box.appendChild(el("p", "trace-empty", "没有待批的动作。"));
      host.appendChild(box);
      return;
    }

    for (const item of pending) {
      const row = el("div", "trace-approval");
      row.appendChild(el("div", "trace-approval-head", item.id + " · " + (item.tool || "")));
      row.appendChild(el("div", "trace-approval-summary", item.summary || "(无摘要)"));
      row.appendChild(el("div", "trace-approval-meta",
        (item.key || "-") + " · 剩 " + (item.expiresInSeconds || 0) + " 秒 · v" + (item.policyVersion || 0)));

      const actions = el("div", "trace-approval-actions");
      const yes = el("button", "trace-btn ok", "批准");
      yes.type = "button";
      yes.addEventListener("click", () => { decide(item.id, true); });
      const no = el("button", "trace-btn bad", "拒绝");
      no.type = "button";
      no.addEventListener("click", () => { decide(item.id, false); });
      actions.appendChild(yes);
      actions.appendChild(no);
      row.appendChild(actions);
      box.appendChild(row);
    }

    host.appendChild(box);
  }

  /// 渲染一屏。payload 就是 /api/traces 的返回（服务端已按脱敏开关处理过 key）。
  function render(payload, approvalsPayload) {
    approvals = approvalsPayload === undefined ? approvals : approvalsPayload;

    if (!payload || payload.available !== true) {
      runs = [];
      drawSummary(payload);
      drawKeys([]);
      drawTimeline([]);
      drawDetail([]);
      return;
    }

    runs = payload.traces || [];
    lastCapacity = payload.capacity || 0;
    lastActive = payload.active || 0;

    const keys = [];
    for (const run of runs) {
      if (run.key && keys.indexOf(run.key) < 0) keys.push(run.key);
    }

    if (keys.indexOf(selectedKey) < 0) selectedKey = keys.length > 0 ? keys[0] : "";

    const visible = runs.filter((r) => selectedKey === "" || r.key === selectedKey);
    if (!visible.some((r) => r.runId === selectedRunId)) selectedRunId = visible.length > 0 ? visible[0].runId : "";

    drawSummary(payload);
    drawKeys(keys);
    drawTimeline(visible);
    drawDetail(visible);
  }

  window.TracePage = { render: render, decide: decide };
})();
