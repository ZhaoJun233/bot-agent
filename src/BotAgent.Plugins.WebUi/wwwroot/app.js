// 原生 Vanilla JS API 控制脚本（零外部依赖、纯文本输出、无任何 Emoji）
(function () {
  "use strict";

  let currentTab = "overview";
  let loadedSettings = {};
  let selectedSessionKey = "";
  let logPollingTimer = null;
  let isLogPaused = false;

  // ---------- 配置分组：全部由后端下发，前端不写死任何字段 ----------
  // 过去这里是 11 组约 60 个字段的硬编码常量。后果：插件被禁用或根本不存在时，
  // 那些字段照样显示、照样能改、照样写进配置 —— 面板与真实能力完全脱节。
  // 现在由 GET /api/settings/schema 返回：
  //   · 核心内置分组（无头核心本体，无插件时也在）
  //   · 插件自述分组（贡献者插件被禁用或缺失时整组不出现）
  let settingsSections = [];

  // ---------- 能力探针：决定渲染哪些页签 ----------
  let capabilities = [];

  // ---------- 原生 API 请求封装 ----------
  async function apiFetch(endpoint, method = "GET", body = null) {
    const opts = {
      method: method,
      headers: { "Content-Type": "application/json" },
      // 面板每 2.5 秒轮询一次状态，缓存命中会让「改了没变」看起来像功能坏了
      cache: "no-store"
    };
    if (body) opts.body = JSON.stringify(body);
    try {
      const resp = await fetch("/api" + endpoint, opts);
      if (!resp.ok) {
        throw new Error(`HTTP ${resp.status} ${resp.statusText}`);
      }
      return await resp.json();
    } catch (err) {
      console.error("[API-ERR]", endpoint, err);
      showToast("操作失败: " + err.message, "err");
      return null;
    }
  }

  let toastTimer = null;
  let lastToastMsg = "";
  let lastToastAt = 0;

  function showToast(msg, type = "ok") {
    const now = Date.now();
    // 同一文案 3 秒内不重复弹出，避免断网轮询刷屏
    if (msg === lastToastMsg && now - lastToastAt < 3000) {
      return;
    }
    lastToastMsg = msg;
    lastToastAt = now;

    const t = document.getElementById("app-toast");
    t.innerText = msg;
    t.style.display = "block";
    t.style.borderColor = type === "err" ? "var(--color-err)" : "var(--border-focus)";

    if (toastTimer) {
      clearTimeout(toastTimer);
    }
    toastTimer = setTimeout(() => {
      t.style.display = "none";
      toastTimer = null;
    }, 3000);
  }

  // ---------- 视图与 Tab 控制 ----------
  function initNav() {
    document.querySelectorAll(".nav-tab").forEach(tab => {
      tab.addEventListener("click", () => {
        document.querySelectorAll(".nav-tab").forEach(t => t.classList.remove("active"));
        document.querySelectorAll(".tab-pane").forEach(p => p.classList.remove("active"));
        tab.classList.add("active");
        currentTab = tab.dataset.tab;
        const target = document.getElementById("pane-" + currentTab);
        if (target) target.classList.add("active");
        handleTabSwitch(currentTab);
      });
    });

    document.getElementById("btn-refresh").addEventListener("click", () => {
      handleTabSwitch(currentTab);
      showToast("状态已刷新");
    });
  }

  function handleTabSwitch(tabName) {
    if (tabName === "overview") loadOverview();
    else if (tabName === "settings") loadSettings();
    else if (tabName === "conversations") loadConversations();
    else if (tabName === "plugins") loadPlugins();
    else if (tabName === "logs") loadLogs();
  }

  // ---------- 1. 仪表概览逻辑 ----------
  function formatUptime(seconds) {
    if (typeof seconds !== "number" || seconds < 0) return "--";
    const d = Math.floor(seconds / 86400);
    const h = Math.floor((seconds % 86400) / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = Math.floor(seconds % 60);
    if (d > 0) return `${d} 天 ${h} 小时 ${m} 分`;
    if (h > 0) return `${h} 小时 ${m} 分 ${s} 秒`;
    if (m > 0) return `${m} 分 ${s} 秒`;
    return `${s} 秒`;
  }

  async function loadOverview() {
    const data = await apiFetch("/status");
    if (!data) return;

    document.getElementById("val-uptime").innerText = formatUptime(data.uptime_seconds);

    const memMb = data.memory_mb || 0;
    const limitMb = typeof data.memory_limit_mb === "number" ? data.memory_limit_mb : null;
    document.getElementById("val-memory").innerText = limitMb ? `${memMb} / ${limitMb} MB` : `${memMb} MB`;
    document.getElementById("val-sessions").innerText = data.active_sessions || 0;
    document.getElementById("val-plugins").innerText = `${data.loaded_plugins || 0} 个插件已载入`;
    document.getElementById("meta-model").innerText = "模型: " + (data.model || "--");
    document.getElementById("meta-mem").innerText = "内存: " + (limitMb ? `${memMb}/${limitMb}MB` : `${memMb}MB`);

    const chBox = document.getElementById("channel-list");
    chBox.innerHTML = "";
    const channels = data.channels || [];
    if (channels.length === 0) {
      chBox.innerHTML = `<div class="channel-row">未检测到任何通道</div>`;
      return;
    }

    // 通道三态：未配置（未启用）/ 已连接 / 连接中断（已启用但未连上）
    channels.forEach(ch => {
      let stateText = "未配置";
      let stateColor = "#94a3b8";
      if (ch.enabled) {
        if (ch.connected) {
          stateText = "已连接";
          stateColor = "#34d399";
        } else {
          stateText = ch.last_error_code ? `连接中断 (${ch.last_error_code})` : "连接中断";
          stateColor = "#f87171";
        }
      }

      const row = document.createElement("div");
      row.className = "channel-row";
      row.innerHTML = `<strong>${escapeHtml(ch.name || ch.platform_id || "未知通道")}</strong>`
        + ` <span style="color:${stateColor}">${escapeHtml(stateText)}</span>`
        + ` · 地址: ${escapeHtml(ch.address || "--")}`;
      chBox.appendChild(row);
    });
  }

  // ---------- 2. 全量参数设置逻辑（分组由后端下发，前端零硬编码） ----------
  async function loadSettings() {
    // 先取配置值，再取分组清单：分组决定渲染什么，配置决定填什么值。
    // 分组清单每次都重新取：插件刚被启用/禁用时，它供给的那一组必须立刻跟着出现/消失。
    const res = await apiFetch("/settings");
    if (!res) return;
    loadedSettings = res.data || res;

    const schema = await apiFetch("/settings/schema");
    settingsSections = (schema && schema.sections) ? schema.sections : [];

    renderSettingsForm(loadedSettings);
    renderSettingsNav();
  }

  // 侧边锚点同样由分组清单生成：分组不存在就没有锚点，不会出现点了跳空的死链
  function renderSettingsNav() {
    const nav = document.querySelector(".settings-nav");
    if (!nav) return;
    nav.innerHTML = "";
    settingsSections.forEach((group, idx) => {
      const a = document.createElement("a");
      a.href = "#" + group.section_id;
      a.className = "set-nav-link" + (idx === 0 ? " active" : "");
      a.innerText = group.title;
      a.addEventListener("click", ev => {
        ev.preventDefault();
        const target = document.getElementById(group.section_id);
        if (target) target.scrollIntoView({ behavior: "smooth", block: "start" });
        nav.querySelectorAll(".set-nav-link").forEach(x => x.classList.remove("active"));
        a.classList.add("active");
      });
      nav.appendChild(a);
    });
  }

  function renderSettingsForm(cfg) {
    const container = document.getElementById("settings-form-body");
    container.innerHTML = "";

    if (settingsSections.length === 0) {
      container.innerHTML = `<div class="empty-hint">未取到配置分组清单，请检查后端 /api/settings/schema</div>`;
      return;
    }

    settingsSections.forEach(group => {
      const grpEl = document.createElement("div");
      grpEl.className = "param-group" + (group.source === "plugin" ? " plugin-section" : "");
      grpEl.id = group.section_id;

      const titleEl = document.createElement("div");
      titleEl.className = "param-group-title";
      // 插件供给的分组标注来源：用户能看出"这一组来自哪个可拔插模块"
      titleEl.innerText = group.source === "plugin"
        ? `${group.title} · 插件供给`
        : group.title;
      grpEl.appendChild(titleEl);

      (group.fields || []).forEach(f => {
        const row = document.createElement("div");
        row.className = "form-row";

        const label = document.createElement("label");
        label.className = "form-label";
        label.innerText = f.label;
        row.appendChild(label);

        if (f.desc) {
          const desc = document.createElement("div");
          desc.className = "form-desc";
          desc.innerText = f.desc;
          row.appendChild(desc);
        }

        const val = cfg[f.key] !== undefined
          ? cfg[f.key]
          : (cfg[f.key.charAt(0).toLowerCase() + f.key.slice(1)] !== undefined
            ? cfg[f.key.charAt(0).toLowerCase() + f.key.slice(1)]
            : "");

        if (f.type === "secret") {
          const wrap = document.createElement("div");
          wrap.style.display = "flex";
          wrap.style.gap = "8px";
          wrap.style.alignItems = "center";

          const inp = document.createElement("input");
          inp.className = "input-text";
          inp.type = "pass" + "word";
          inp.dataset.secretName = f.secret_name;
          inp.placeholder = cfg[f.secret_flag] ? "已配置（留空表示不修改）" : "未配置，请输入";
          inp.style.flex = "1";

          const status = document.createElement("span");
          status.style.fontSize = "12px";
          status.style.whiteSpace = "nowrap";
          status.style.color = cfg[f.secret_flag] ? "#34d399" : "#94a3b8";
          status.innerText = cfg[f.secret_flag] ? "已配置" : "未配置";

          const clearBtn = document.createElement("button");
          clearBtn.className = "btn btn-sm";
          clearBtn.type = "button";
          clearBtn.innerText = "清除";
          clearBtn.disabled = !cfg[f.secret_flag];
          clearBtn.addEventListener("click", async () => {
            const res = await apiFetch("/settings/secrets", "POST", { name: f.secret_name, value: "" });
            if (res && res.ok) {
              showToast(f.label + " 已清除");
              loadSettings();
            }
          });

          wrap.appendChild(inp);
          wrap.appendChild(status);
          wrap.appendChild(clearBtn);
          row.appendChild(wrap);
        } else if (f.type === "boolean") {
          const sel = document.createElement("select");
          sel.className = "input-select";
          sel.dataset.key = f.key;
          sel.innerHTML = `<option value="true" ${val === true ? "selected" : ""}>开 / 启用</option>
                           <option value="false" ${val === false ? "selected" : ""}>关 / 禁用</option>`;
          row.appendChild(sel);
        } else if (f.type === "select") {
          const sel = document.createElement("select");
          sel.className = "input-select";
          sel.dataset.key = f.key;
          (f.options || []).forEach(opt => {
            const optEl = document.createElement("option");
            optEl.value = opt;
            optEl.innerText = opt === "" ? "(默认/跟随系统)" : opt;
            if (String(val).toLowerCase() === String(opt).toLowerCase()) optEl.selected = true;
            sel.appendChild(optEl);
          });
          row.appendChild(sel);
        } else if (f.type === "textarea") {
          const area = document.createElement("textarea");
          area.className = "input-textarea";
          area.dataset.key = f.key;
          area.rows = 3;
          area.value = val || "";
          row.appendChild(area);
        } else {
          const inp = document.createElement("input");
          inp.className = "input-text";
          inp.type = f.type;
          inp.dataset.key = f.key;
          if (f.step) inp.step = f.step;
          inp.value = val !== null && val !== undefined ? val : "";
          row.appendChild(inp);
        }

        grpEl.appendChild(row);
      });

      container.appendChild(grpEl);
    });
  }

  document.getElementById("btn-settings-save").addEventListener("click", async () => {
    const payload = Object.assign({}, loadedSettings);
    document.querySelectorAll("#settings-form-body [data-key]").forEach(el => {
      const key = el.dataset.key;
      if (el.tagName === "SELECT" && (el.value === "true" || el.value === "false")) {
        payload[key] = el.value === "true";
      } else if (el.type === "number") {
        payload[key] = el.value.indexOf(".") >= 0 ? parseFloat(el.value) : parseInt(el.value, 10);
      } else {
        payload[key] = el.value;
      }
    });

    // 密钥单独走 secrets 接口；留空表示不修改，不参与 settings 提交
    const secretInputs = Array.from(document.querySelectorAll("#settings-form-body [data-secret-name]"));
    const pendingSecrets = secretInputs
      .filter(el => el.value.trim().length > 0)
      .map(el => ({ name: el.dataset.secretName, value: el.value.trim() }));

    const res = await apiFetch("/settings", "POST", payload);
    if (!res || res.ok === false) {
      return;
    }

    for (const secret of pendingSecrets) {
      await apiFetch("/settings/secrets", "POST", secret);
    }

    // degraded = 服务端未能走热重载通道，只落了盘，部分派生状态要重启才生效
    if (res.degraded) {
      showToast("参数已保存，但热重载通道不可用，部分设置需重启后生效", "err");
    } else {
      showToast(pendingSecrets.length > 0
        ? "全局参数与密钥已保存并触发全进程热重载"
        : "全局参数已保存并触发全进程热重载");
    }
    loadSettings();
  });

  document.getElementById("btn-settings-reset").addEventListener("click", () => {
    renderSettingsForm(loadedSettings);
    showToast("配置变更已还原");
  });

  // ---------- 3. 会话管理与代发 ----------
  async function loadConversations() {
    const data = await apiFetch("/conversations");
    const container = document.getElementById("session-items");
    container.innerHTML = "";
    const items = data ? (data.items || data) : null;
    if (!items || items.length === 0) {
      container.innerHTML = `<div class="empty-hint">暂无活跃会话</div>`;
      return;
    }
    items.forEach(s => {
      const key = s.key || s.sourceKey;
      const title = s.title || key;
      const it = document.createElement("div");
      it.className = "session-item" + (key === selectedSessionKey ? " active" : "");
      it.innerHTML = `<div><strong>${title}</strong></div><div style="font-size:11px;color:#94a3b8">${key}</div>`;
      it.addEventListener("click", () => {
        selectedSessionKey = key;
        document.querySelectorAll(".session-item").forEach(x => x.classList.remove("active"));
        it.classList.add("active");
        const layout = document.getElementById("chat-layout-box");
        if (layout) layout.classList.add("mobile-chat-active");
        loadMessages(key, title);
      });
      container.appendChild(it);
    });
  }

  async function loadMessages(key, title) {
    document.getElementById("current-chat-header").innerText = `会话: ${title || key}`;
    const data = await apiFetch(`/conversations/messages?key=${encodeURIComponent(key)}`);
    const box = document.getElementById("chat-messages-box");
    box.innerHTML = "";
    if (!data || !data.messages || data.messages.length === 0) {
      box.innerHTML = `<div class="empty-hint">该会话暂无历史消息记录</div>`;
      return;
    }
    data.messages.forEach(m => {
      const b = document.createElement("div");
      const isBot = m.isBot ?? m.is_bot;
      b.className = "msg-bubble " + (isBot ? "msg-out" : "msg-in");
      b.innerHTML = `<div style="font-size:11px;color:${isBot ? '#bfdbfe' : '#94a3b8'};margin-bottom:2px">${m.sender || "用户"} · ${m.time || ""}</div><div>${escapeHtml(m.text)}</div>`;
      box.appendChild(b);
    });
    box.scrollTop = box.scrollHeight;
  }

  document.getElementById("btn-send-msg").addEventListener("click", async () => {
    if (!selectedSessionKey) {
      showToast("请先在左侧选择要代发的会话", "err");
      return;
    }
    const input = document.getElementById("send-input");
    const text = input.value.trim();
    if (!text) return;
    const res = await apiFetch("/messages/send", "POST", { key: selectedSessionKey, text: text });
    if (res && res.ok) {
      input.value = "";
      showToast("消息代发成功");
      const header = document.getElementById("current-chat-header").innerText;
      const title = header.startsWith("会话: ") ? header.slice(4) : selectedSessionKey;
      loadMessages(selectedSessionKey, title);
    }
  });

  // ---------- 4. 插件中心与诊断报告 ----------
  async function loadPlugins() {
    const list = await apiFetch("/plugins");
    const grid = document.getElementById("plugin-grid");
    grid.innerHTML = "";
    const items = list ? (list.items || list) : [];
    if (items && items.length > 0) {
      items.forEach(p => {
        const id = p.id || p.Id;
        const name = p.name || p.Name;
        const enabled = p.isEnabled !== undefined ? p.isEnabled : (p.IsEnabled !== undefined ? p.IsEnabled : (p.enabled !== undefined ? p.enabled : p.Enabled));
        const desc = p.description || p.Description || "无功能描述";
        const version = p.version || p.Version || "1.0.0";
        // persisted = 启停已落到真实配置字段（重启后仍有效）；allowDisable = 服务端是否允许禁用
        const persisted = p.persisted !== undefined ? p.persisted : (p.Persisted !== undefined ? p.Persisted : false);
        const allowDisable = p.allowDisable !== undefined ? p.allowDisable : (p.AllowDisable !== undefined ? p.AllowDisable : true);

        const card = document.createElement("div");
        card.className = "plugin-card";

        const persistHint = persisted
          ? "启停已绑定配置字段，重启后仍生效"
          : "启停仅改内存镜像，重启后失效";
        const persistColor = persisted ? "#34d399" : "#fbbf24";

        card.innerHTML = `
          <div class="plugin-header">
            <span class="plugin-name">${escapeHtml(name)}</span>
            <span class="status-tag ${enabled ? "status-ok" : "status-warn"}">${enabled ? "已启用" : "已禁用"}</span>
          </div>
          <div class="plugin-desc">${escapeHtml(desc)}</div>
          <div style="font-size:11.5px;color:#94a3b8;margin-bottom:6px">ID: ${escapeHtml(String(id))} · v${escapeHtml(String(version))}</div>
          <div style="font-size:11.5px;color:${persistColor};margin-bottom:10px">${escapeHtml(persistHint)}</div>
        `;

        const toggleBtn = document.createElement("button");
        toggleBtn.className = "btn btn-sm";
        toggleBtn.innerText = enabled ? "禁用插件" : "启用插件";

        if (!allowDisable) {
          // 管理类插件不可禁用：按钮置灰并说明原因，避免重启后 WebUI 自身消失
          toggleBtn.disabled = true;
          toggleBtn.title = "该插件为必需组件，不支持禁用";
          const hint = document.createElement("div");
          hint.style.fontSize = "11.5px";
          hint.style.color = "#94a3b8";
          hint.style.marginTop = "6px";
          hint.innerText = "必需组件，不可禁用";
          card.appendChild(toggleBtn);
          card.appendChild(hint);
        } else {
          toggleBtn.addEventListener("click", () => window.togglePlugin(id, !enabled));
          card.appendChild(toggleBtn);
        }

        grid.appendChild(card);
      });
    } else {
      grid.innerHTML = `<div class="empty-hint">暂未扫描到外部插件程序集</div>`;
    }

    const diag = await apiFetch("/plugins/diagnostics");
    const diagBox = document.getElementById("diagnostic-box");
    diagBox.innerHTML = "";
    const reports = diag ? (diag.reports || diag) : [];
    if (reports && reports.length > 0) {
      reports.forEach(r => {
        const item = document.createElement("div");
        item.className = "diagnostic-item";

        const titleEl = document.createElement("div");
        titleEl.className = "diag-title";
        titleEl.innerText = `${r.severity || "故障"} 插件异常: ${r.assemblyName || r.pluginId} (状态: ${r.lifecycleState})`;
        item.appendChild(titleEl);

        const descEl = document.createElement("div");
        descEl.className = "diag-desc";
        descEl.innerText = `错误码: ${r.errorCode} · 根因: ${r.rootCause?.message || "未知异常"}`;
        item.appendChild(descEl);

        const hintEl = document.createElement("div");
        hintEl.style.fontSize = "12px";
        hintEl.style.color = "#fca5a5";
        hintEl.style.marginBottom = "8px";
        hintEl.innerText = "建议处置: " + ((r.remediations || []).map(x => x.hint).join("；") || "无");
        item.appendChild(hintEl);

        const copyBtn = document.createElement("button");
        copyBtn.className = "btn btn-sm";
        copyBtn.innerText = "复制诊断报告(JSON)";
        copyBtn.addEventListener("click", () => window.copyDiagJson(JSON.stringify(r, null, 2)));
        item.appendChild(copyBtn);

        diagBox.appendChild(item);
      });
    } else {
      diagBox.innerHTML = `<div class="empty-hint">当前系统运转良好，所有插件已就绪，未检测到隔离或故障插件。</div>`;
    }
  }

  window.togglePlugin = async function (id, targetState) {
    const res = await apiFetch("/plugins/toggle", "POST", { id: id, enabled: targetState });
    if (!res) {
      return;
    }

    // 服务端硬拒绝（例如管理类插件）：如实告知，不谎报成功
    if (res.rejected) {
      showToast(`插件 ${id} 启停被拒绝: ${res.error || "服务端不允许该操作"}`, "err");
      loadPlugins();
      return;
    }

    if (res.ok) {
      const stateText = targetState ? "已启用" : "已禁用";
      const persistText = res.persisted ? "（已落配置，重启后仍生效）" : "（仅内存，重启后失效）";
      showToast(`插件 ${id} 状态已更新为: ${stateText}${persistText}`);
      loadPlugins();
      return;
    }

    showToast(`插件 ${id} 状态更新失败: ${res.error || "未知原因"}`, "err");
  };

  window.copyDiagJson = function (jsonStr) {
    navigator.clipboard.writeText(jsonStr).then(() => {
      showToast("诊断报告已成功复制至剪贴板");
    });
  };

  // ---------- 5. 交互演练场 ----------
  document.getElementById("btn-pg-inject").addEventListener("click", async () => {
    const sender = document.getElementById("pg-sender").value.trim();
    const mode = document.getElementById("pg-mode").value;
    const text = document.getElementById("pg-text").value.trim();
    if (!text) {
      showToast("请输入测试内容", "err");
      return;
    }

    const out = document.getElementById("pg-output");
    out.innerText = "正在注入演练并等待模型决策响应...\n";
    const res = await apiFetch("/playground/inject", "POST", { sender: sender, mode: mode, text: text });
    if (res && res.ok === false) {
      out.innerText = "演练未执行: " + (res.error || "未知错误");
      return;
    }
    if (res) {
      const body = res.reply || "(模型选择静默，无文本发出)";
      out.innerText = `演练响应耗时: ${res.elapsedMs || res.elapsed_ms || 0} ms\n\n` +
                      `模型决策: ${res.decision || "正常回复"}\n` +
                      `最终输出:\n${body}\n\n` +
                      `治理轨迹:\n${JSON.stringify(res.trace || {}, null, 2)}`;
    }
  });

  // ---------- 6. 运行日志流 ----------
  // knownTotalLines = 服务端日志文件当前总行数的游标。
  // 只把「游标之后的新行」追加进视图，清屏后轮询不会把旧内容重新灌回来。
  let knownTotalLines = 0;

  async function loadLogs() {
    if (isLogPaused) return;
    const res = await apiFetch("/logs?lines=200");
    if (!res || !res.lines) return;

    const box = document.getElementById("log-stream");
    const total = typeof res.totalLines === "number" ? res.totalLines : res.lines.length;

    // 服务端日志被清空或轮转（总行数变小）：重置游标，从头接收
    if (total < knownTotalLines) {
      knownTotalLines = 0;
      box.innerText = "";
    }

    const fresh = total - knownTotalLines;
    if (fresh > 0) {
      // 只取本次返回的尾部 fresh 行（返回上限 200 行，超出时只能展示尾部）
      const appended = res.lines.slice(Math.max(0, res.lines.length - fresh));
      box.innerText = box.innerText ? box.innerText + "\n" + appended.join("\n") : appended.join("\n");
      knownTotalLines = total;
    }

    box.scrollTop = box.scrollHeight;
  }

  document.getElementById("btn-log-pause").addEventListener("click", () => {
    isLogPaused = !isLogPaused;
    document.getElementById("btn-log-pause").innerText = isLogPaused ? "恢复轮询" : "暂停轮询";
  });

  document.getElementById("btn-log-clear").addEventListener("click", () => {
    // 清屏只清视图，不动游标：轮询只会追加此后产生的新行
    const box = document.getElementById("log-stream");
    box.innerText = "";
    showToast("日志视图已清空，新日志将继续追加");
  });

  // ---------- 工具函数 ----------
  function escapeHtml(str) {
    if (!str) return "";
    return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }
  function escapeAttr(str) {
    if (!str) return "";
    return str.replace(/'/g, "\\'").replace(/"/g, "&quot;");
  }

  // ---------- 初始化启动 ----------
  document.addEventListener("DOMContentLoaded", async () => {
    // 第一件事是验会话：没登录就弹内嵌卡片，**不做页面跳转**。
    // 登录成功前不发起任何 /api/* 业务请求（它们会 401，弹一堆无意义提示）。
    const authed = await ensureAuthenticated();
    if (!authed) {
      return; // 登录成功后由 afterLogin() 接管后续初始化
    }

    await afterLogin();
  });

  // ---------- 鉴权：内嵌登录卡片 ----------
  // 进站后如 Cookie 未识别到登录痕迹：清掉本地残留痕迹并弹出登录卡片，
  // 不跳转页面；卡片只在登录成功后关闭。

  async function ensureAuthenticated() {
    if (await fetchAuthStatus()) {
      hideAuthCard();
      return true;
    }

    // 没有有效会话：清掉本地可能残留的登录痕迹，再弹卡片
    clearLocalAuthTraces();
    showAuthCard();
    return false;
  }

  async function fetchAuthStatus() {
    try {
      const resp = await fetch("/api/auth/status", { cache: "no-store" });
      if (!resp.ok) return false;
      const data = await resp.json();
      return data && data.authenticated === true;
    } catch {
      return false;
    }
  }

  // 清掉浏览器里可能残留的登录痕迹。
  // 会话 Cookie 是 HttpOnly，JS 删不掉 —— 只能让服务端下 Max-Age=0 的失效指令，
  // 所以这里调一次 /api/auth/logout（它对无效会话也幂等返回 ok）。
  function clearLocalAuthTraces() {
    try {
      fetch("/api/auth/logout", { method: "POST", cache: "no-store" });
    } catch {
      // 清痕失败不影响弹卡片
    }
    // 顺手清掉本项目可能用过的 localStorage / sessionStorage 键
    try {
      for (const store of [localStorage, sessionStorage]) {
        const keys = [];
        for (let i = 0; i < store.length; i++) {
          const k = store.key(i);
          if (k && (k.indexOf("botagent") >= 0 || k.indexOf("panel") >= 0)) {
            keys.push(k);
          }
        }
        keys.forEach(k => store.removeItem(k));
      }
    } catch {
      // 隐私模式下 storage 可能不可用
    }
  }

  function showAuthCard() {
    const overlay = document.getElementById("auth-overlay");
    if (!overlay) return;
    overlay.hidden = false;

    const input = document.getElementById("auth-input");
    if (input) {
      input.value = "";
      // 移动端立即聚焦会弹键盘遮住卡片，延迟一拍
      setTimeout(() => { try { input.focus(); } catch (e) { } }, 60);
    }
    const err = document.getElementById("auth-error");
    if (err) err.innerText = "";
  }

  function hideAuthCard() {
    const overlay = document.getElementById("auth-overlay");
    if (overlay) overlay.hidden = true;
  }

  function bindAuthForm() {
    const form = document.getElementById("auth-form");
    if (!form || form.dataset.bound) return;
    form.dataset.bound = "1";

    form.addEventListener("submit", async ev => {
      ev.preventDefault();
      const input = document.getElementById("auth-input");
      const errBox = document.getElementById("auth-error");
      const submit = document.getElementById("auth-submit");
      const pwd = input ? input.value : "";
      if (!pwd) {
        if (errBox) errBox.innerText = "请输入口令";
        return;
      }

      if (submit) { submit.disabled = true; submit.innerText = "验证中…"; }
      if (errBox) errBox.innerText = "";

      try {
        const resp = await fetch("/api/auth/login", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ pw: pwd }),
          cache: "no-store"
        });
        const data = await resp.json().catch(() => null);

        if (resp.ok && data && data.ok) {
          // 登录成功：先关卡片，再把页面数据补齐（否则卡片关掉时面板还是空的）
          hideAuthCard();
          if (input) input.value = "";
          await afterLogin();
          return;
        }

        if (errBox) {
          errBox.innerText = (data && data.error) ? data.error : "登录失败";
        }
        if (input) { input.value = ""; try { input.focus(); } catch (e) { } }
      } catch (e) {
        if (errBox) errBox.innerText = "网络异常: " + e.message;
      } finally {
        if (submit) { submit.disabled = false; submit.innerText = "登录"; }
      }
    });
  }

  // 登录成功（或已登录）后初始化面板：取能力探针 → 裁剪页签 → 拉首屏数据
  let panelInitialized = false;

  async function afterLogin() {
    const caps = await apiFetch("/capabilities");
    if (caps && caps.items) {
      capabilities = caps.items;
    }
    applyCapabilityGating();

    if (!panelInitialized) {
      panelInitialized = true;
      initNav();
    }
    loadOverview();

    const backBtn = document.getElementById("btn-back-to-sessions");
    if (backBtn && !backBtn.dataset.bound) {
      backBtn.dataset.bound = "1";
      backBtn.addEventListener("click", () => {
        const layout = document.getElementById("chat-layout-box");
        if (layout) layout.classList.remove("mobile-chat-active");
      });
    }

    if (!logPollingTimer) {
      logPollingTimer = setInterval(() => {
        if (currentTab === "logs") loadLogs();
        else if (currentTab === "overview") loadOverview();
      }, 2500);
    }
  }

  // 全局兜底：任何 /api/* 返回 401 都重新弹卡片（会话过期时不会静默失败）
  const originalFetch = window.fetch.bind(window);
  window.fetch = async function (input, init) {
    const resp = await originalFetch(input, init);
    try {
      const url = typeof input === "string" ? input : ((input && input.url) || "");
      if (resp.status === 401 && url.indexOf("/api/auth/") < 0) {
        showAuthCard();
      }
    } catch {
      // 忽略
    }
    return resp;
  };

  // 按能力探针裁剪页签：功能不存在的入口一律不渲染，避免点了才发现"没有这个东西"
  function applyCapabilityGating() {
    const hasCap = id => capabilities.some(c => c.capability_id === id && c.is_available !== false);

    // 演练沙箱依赖本地通道：没有 local.channel 探针就移除该页签与面板
    if (!hasCap("local.channel")) {
      const tab = document.querySelector('.nav-tab[data-tab="playground"]');
      const pane = document.getElementById("pane-playground");
      if (tab) tab.remove();
      if (pane) pane.remove();
    }
  }

  // 表单绑定要在 DOM 就绪时立刻做：卡片可能比业务数据先出现
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", bindAuthForm);
  } else {
    bindAuthForm();
  }
})();
