/* ══════════════════════════════════════════════════════════════
   QQ Chat Agent · Web 面板
   与桌面版 ViewModel 对应的前端状态机：
     会话列表 / 消息气泡 / AI 开关 / 设置持久化 / SSE 实时同步
   ══════════════════════════════════════════════════════════════ */
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);

  // 桌面版 ChatPageViewModel.GradientPairs：无 QQ 头像时的渐变底色
  const GRADIENTS = [
    ["#5E5CE6", "#8E6CEF"], ["#0EA5E9", "#22D3EE"], ["#10B981", "#34D399"],
    ["#F59E0B", "#FBBF24"], ["#EF4444", "#F97316"], ["#EC4899", "#8B5CF6"]
  ];

  const state = {
    conversations: [],
    byKey: new Map(),
    activeKey: null,
    messages: new Map(),      // key -> [msg]
    status: null,
    aiMode: true,
    search: "",
    logs: [],
    settingsLoaded: false,    // 设置表单是否已从服务端回填过
    agentPromptDefault: "",  // 服务端那份默认「Agent 附加提示词」（面板「恢复默认」按钮用，不在前端抄一份）
    chatOpen: false,          // 手机端：是否已点进某个会话（列表 ↔ 聊天 的主从切换）
    login: {                  // 扫码登录卡片
      qr: null,               // /api/qqlogin 的响应
      collapsed: false,       // 用户手动收起过
      greeted: false          // 上线提示只说一次
    },
    stickers: null            // 表情包库（打开弹层时拉）
  };

  /* ─────────── 工具 ─────────── */

  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  function timeText(ms) {
    const d = new Date(ms);
    const now = new Date();
    const sameDay = d.toDateString() === now.toDateString();
    const p = (n) => String(n).padStart(2, "0");
    return sameDay
      ? `${p(d.getHours())}:${p(d.getMinutes())}`
      : `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  function hashIndex(key) {
    let h = 0;
    for (let i = 0; i < key.length; i++) h = (h * 31 + key.charCodeAt(i)) >>> 0;
    return h % GRADIENTS.length;
  }

  function toast(text) {
    const el = $("toast");
    el.textContent = text;
    el.hidden = false;
    clearTimeout(el._t);
    el._t = setTimeout(() => { el.hidden = true; }, 2200);
  }

  /* ─────────── 面板访问令牌（可选） ───────────
     服务端设了 QQCHAT_PANEL_TOKEN 后，面板需要令牌。
     首次用 http://…/?token=你的令牌 打开即可，之后自动记住。 */
  const TOKEN_KEY = "qqchat.panel.token";

  (function captureTokenFromUrl() {
    try {
      const url = new URL(location.href);
      const t = url.searchParams.get("token");
      if (!t) return;
      localStorage.setItem(TOKEN_KEY, t.trim());
      url.searchParams.delete("token");
      history.replaceState(null, "", url.pathname + url.search + url.hash);
    } catch { /* 无 localStorage / 无 history：忽略 */ }
  })();

  function panelToken() {
    try { return localStorage.getItem(TOKEN_KEY) || ""; } catch { return ""; }
  }

  /// EventSource 不能自定义请求头 → 令牌只能走查询参数
  /// 面板自身的基地址（下载链接用；带面板令牌时也会拼上）
  function apiBase() {
    return location.origin;
  }

  function withToken(path) {
    const t = panelToken();
    if (!t) return path;
    return path + (path.includes("?") ? "&" : "?") + "token=" + encodeURIComponent(t);
  }

  function authHeaders(extra) {
    const t = panelToken();
    return Object.assign({}, extra || {}, t ? { "X-Panel-Token": t } : {});
  }

  /// 401 时向用户要一次令牌（避免直接报错让人摸不着头脑）
  function askForToken() {
    const t = prompt("面板需要访问令牌（服务端设置了 QQCHAT_PANEL_TOKEN）：");
    if (!t) return;
    try { localStorage.setItem(TOKEN_KEY, t.trim()); } catch { /* 忽略 */ }
    location.reload();
  }

  async function api(path, options) {
    const opts = options || {};
    const res = await fetch(withToken(path), {
      ...opts,
      headers: authHeaders(Object.assign({ "Content-Type": "application/json" }, opts.headers))
    });
    if (res.status === 401) { askForToken(); throw new Error("需要访问令牌"); }
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { /* 非 JSON */ }
    if (!res.ok) throw Object.assign(new Error(`HTTP ${res.status}`), { status: res.status, data });
    return data;
  }

  /* ─────────── 手机端主从切换 ───────────
     窄屏下会话列表与聊天区不再并排（一屏放不下）：
     默认只看列表，点进会话才切到聊天，头部有返回键，底部有标签栏。
     桌面端一切照旧 —— 这里只是给 <body> 挂一个 class，由 CSS 媒体查询决定要不要理它。 */
  function syncMobileView() {
    document.body.classList.toggle("m-chat-open", !!state.chatOpen);
  }

  /* ─────────── 表情包库（面板） ───────────
     库是全局共用一份（不分会话）。面板只负责“看”和“手动干预”：
     自动收集/按语境发送/自巡检都在机器人那边，这里只看结果。 */

  async function openStickerLib() {
    $("stickerModal").hidden = false;
    $("stickerGrid").textContent = "";
    const empty = document.createElement("div");
    empty.className = "sticker-empty";
    empty.textContent = "加载中…";
    $("stickerGrid").appendChild(empty);
    await refreshStickerLib();
  }

  async function refreshStickerLib() {
    try {
      state.stickers = await api("/api/stickers");
      renderStickerLib();
    } catch (e) {
      $("stickerStat").textContent = "读取失败：" + e.message;
    }
  }

  function renderStickerLib() {
    const d = state.stickers || { items: [] };
    $("stickerStat").textContent =
      `共 ${d.count}/${d.max} 张（已识别 ${d.described}，待识别 ${d.pendingDescribe}）`;

    const grid = $("stickerGrid");
    grid.textContent = "";

    const items = d.items || [];
    if (items.length === 0) {
      const empty = document.createElement("div");
      empty.className = "sticker-empty";
      empty.textContent =
        "还没有表情包。群友在群里发表情/图片时会自动收进来（去重 + 自动识别）；也可以点上面的「导入收藏表情」。";
      grid.appendChild(empty);
      return;
    }

    for (const item of items) {
      const cell = document.createElement("div");
      cell.className = "sticker-cell";

      const img = document.createElement("img");
      img.alt = "";
      img.loading = "lazy";
      img.src = withToken(`/api/stickers/${encodeURIComponent(item.id)}/img`);
      img.addEventListener("error", () => img.remove());

      const cap = document.createElement("div");
      cap.className = "cap";
      const tags = (item.tags || []).join("/");
      cap.textContent = (item.desc || "（还没识别）") + (tags ? "｜" + tags : "");
      const uses = document.createElement("span");
      uses.className = "uses";
      uses.textContent = ` ·用过${item.uses}次`;
      cap.appendChild(uses);

      const del = document.createElement("button");
      del.className = "del";
      del.textContent = "✕";
      del.title = "删除这张表情包";
      del.addEventListener("click", async () => {
        if (!confirm("删除这张表情包？")) return;
        try {
          await api(`/api/stickers/${encodeURIComponent(item.id)}/delete`, { method: "POST" });
          await refreshStickerLib();
        } catch (e) {
          toast("删除失败：" + e.message);
        }
      });

      cell.appendChild(img);
      cell.appendChild(cap);
      cell.appendChild(del);
      grid.appendChild(cell);
    }
  }

  /// 让机器人自己巡检一遍（它会看一遍库，自己决定删哪些）
  async function requestStickerCurate() {
    try {
      await api("/api/stickers/curate", { method: "POST" });
      toast("已开始巡检，结果会记在运行日志里");
      setTimeout(refreshStickerLib, 4000);
    } catch (e) {
      toast("巡检失败：" + e.message);
    }
  }

  /// 从登录账号的 QQ 收藏表情导入（机器人自己“添加”表情包的来源）
  async function requestStickerImport() {
    try {
      await api("/api/stickers/import", { method: "POST" });
      toast("正在从 QQ 收藏表情导入，稍后刷新看结果");
      setTimeout(refreshStickerLib, 6000);
    } catch (e) {
      toast("导入失败：" + e.message);
    }
  }

  /* ─────────── 会话列表 ─────────── */

  // 按 key 复用 DOM 节点，避免每次事件都重建整个列表（重建会重载头像图、强制重排）
  const convNodes = new Map();
  let convSignature = "";

  // 外部设备表（面板里「外部设备」那张表）的**内存副本**：
  //   · 设备表自己不在 DOM-id 那套表单字段里，所以保存时得单独把它拼进 payload（saveSettings 用）；
  //   · 声明在外层作用域，因为填表的是 bindUi()、而读表的是 saveSettings() —— 各自在其他地方都看不见对方。
  //   · loaded 这个标记是为了防“设备表没拉到 + 点保存 = 把设备配置写成空”。
  let agentDevices = [];
  let agentDevicesLoaded = false;
  // 上一份与服务端对齐的设备表：重新进设置页 / 放弃修改时用它还原草稿。
  // 只有 loaded 标记是不够的 —— 草稿留在内存里的话，下次保存会把“已经放弃的改动”一起提交（实测踩过）。
  let agentDevicesSaved = [];
  // 用户在面板里加/删/改过设备行（还没有权威设备表时，保存必须明确告知“设备改动没保存”）
  let agentDevicesEdited = false;
  // 本次保存是否因为“没拉到设备表”而跳过了设备改动（只在保存提示里说一句）
  let deviceTableSkipped = false;
  // 任何一次表单改动都 +1。保存请求发出去之后用户又改了东西时，这次刷新结果已经过时，
  // 用它回填会把新编辑盖掉（实测踩过）。
  let settingsEditSeq = 0;

  // 保存设置后要顺手刷新的东西（由 bindUi() 注册进来；没注册就跳过）。
  // 用钩子而不是直接调用的原因：refreshAgentDevicesFull 定义在 bindUi() **内部**，而 saveSettings() 在外层 ——
  // 直接 `await refreshAgentDevicesFull()` 就是 ReferenceError，被 catch 成一句“保存失败”
  // （其实服务器那边已经存上了，报错完全指错方向；实测踩过）。
  let afterSettingsSaved = null;
  // bindUi() 注册：把设备草稿画出来（顶层的 loadAgentDevices() 看不见绑在 DOM 上的那套渲染代码）
  let renderAgentDeviceList = null;
  // 全局默认工作目录（面板里那个「工作目录」）：设备行用它标注“这个目录是哪来的”。
  let agentGlobalWorkdir = "";

  function convSignatureOf(items) {
    return items.map((c) =>
      `${c.key}|${c.name}|${c.preview}|${c.unread}|${c.thinking ? 1 : 0}|${c.lastTime}`
    ).join("~") + `#${state.activeKey}#${state.search}`;
  }

  function createConvNode(c) {
    const el = document.createElement("div");
    el.className = "conv";
    el.dataset.key = c.key;
    el.innerHTML =
      `<div class="avatar av-42"><span class="av-text"></span><img alt="" loading="lazy" />` +
      `<div class="conv-main"><div class="conv-row"><span class="conv-name"></span>` +
      `<span class="conv-time"></span></div><div class="conv-row">` +
      `<span class="conv-preview"></span><span class="conv-tail"></span></div></div>`;
    const img = el.querySelector("img");
    img.addEventListener("error", () => { img.remove(); });
    el.addEventListener("click", () => {
      // 长按弹菜单后浏览器还会补一个 click：别让它顺手把会话切走
      if (el.dataset.suppressClick === "1") { el.dataset.suppressClick = "0"; return; }
      selectConversation(el.dataset.key);
    });
    el.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      openContextMenu(e.clientX, e.clientY, el.dataset.key);
    });

    // 手机没有右键：长按 450ms 弹同一张菜单（iOS Safari 不触发 contextmenu）
    let pressTimer = 0;
    const cancelPress = () => { if (pressTimer) { clearTimeout(pressTimer); pressTimer = 0; } };
    el.addEventListener("touchstart", (e) => {
      const t = e.touches && e.touches[0];
      if (!t) return;
      cancelPress();
      pressTimer = setTimeout(() => {
        pressTimer = 0;
        el.dataset.suppressClick = "1";
        openContextMenu(t.clientX, t.clientY, el.dataset.key);
      }, 450);
    }, { passive: true });
    el.addEventListener("touchend", cancelPress, { passive: true });
    el.addEventListener("touchcancel", cancelPress, { passive: true });
    el.addEventListener("touchmove", cancelPress, { passive: true });
    return el;
  }

  function updateConvNode(el, c) {
    const [g1, g2] = GRADIENTS[hashIndex(c.key)];
    el.classList.toggle("active", c.key === state.activeKey);

    // 通道徽标：官方那条在名字前挂个小标，一眼看出这条会话属于哪个板块
    const nameEl = el.querySelector(".conv-name");
    if (c.channelTag && nameEl.dataset.tag !== c.channelTag) {
      const tag = document.createElement("span");
      tag.className = "chan-badge chan-" + (c.channel || "private");
      tag.textContent = c.channelTag;
      nameEl.textContent = "";
      nameEl.appendChild(tag);
      nameEl.appendChild(document.createTextNode(c.name || ""));
      nameEl.dataset.tag = c.channelTag;
    } else if (c.channelTag) {
      // 徽标已经在，只换名字文本（SSE 高频推送时别每次都重建节点）
      const text = nameEl.lastChild;
      if (text && text.nodeValue !== c.name) text.nodeValue = c.name || "";
    } else if (nameEl.textContent !== (c.name || "")) {
      nameEl.textContent = c.name || "";
    }

    const av = el.querySelector(".avatar");
    av.style.background = `linear-gradient(135deg,${g1},${g2})`;
    el.querySelector(".av-text").textContent = c.avatarText;

    // 头像地址未变就不动 img，避免重复下载/闪烁
    const img = av.querySelector("img");
    if (img) {
      const want = c.avatarUrl || "";
      if (img.dataset.url !== want) {
        img.dataset.url = want;
        if (want) img.src = want; else img.remove();
      }
    } else if (c.avatarUrl) {
      const fresh = document.createElement("img");
      fresh.alt = "";
      fresh.loading = "lazy";
      fresh.dataset.url = c.avatarUrl;
      fresh.addEventListener("error", () => fresh.remove());
      fresh.src = c.avatarUrl;
      av.appendChild(fresh);
    }

    el.querySelector(".conv-name").textContent = c.name;
    el.querySelector(".conv-time").textContent = timeText(c.lastTime);
    el.querySelector(".conv-preview").textContent = c.preview || "（暂无消息）";

    const tail = el.querySelector(".conv-tail");
    const wantTail = c.thinking ? "think" : c.unread > 0 ? "badge" : "none";
    if (tail.dataset.kind !== wantTail) {
      tail.dataset.kind = wantTail;
      tail.className = wantTail === "think" ? "conv-think" : wantTail === "badge" ? "badge" : "";
      tail.textContent = "";
    }

    if (wantTail === "badge") {
      const text = c.unread > 99 ? "99+" : String(c.unread);
      if (tail.textContent !== text) tail.textContent = text;
      tail.className = "badge";
    } else if (wantTail === "think" && tail.textContent !== "思考中…") {
      tail.textContent = "思考中…";
    }
  }

  // 两条通道的状态一行字。**只在官方通道启用时才显示**：
// 没启用时这行只会重复“私域在线”（顶栏已经有连接状态了），反而显得吵。
function renderChannelStatus(channels) {
  state.channels = channels || [];
  const box = $("chanStatus");
  const official = state.channels.find((c) => c.channel === "official");
  const parts = state.channels.map((c) => {
    const st = !c.enabled ? "未启用" : (c.connected ? "在线" : "离线");
    return `${c.name} ${st}`;
  });
  box.textContent = parts.join(" · ");
  box.hidden = !official || !official.enabled;
  const tab = $("chanTabOfficial");
  tab.title = !official || !official.enabled
    ? "官方商用通道未启用（面板设置里开一下，并配好 appid/secret）"
    : (official.connected ? "官方商用通道在线" : "官方商用通道已启用，但还没连上");
  // 没启用时把 Tab 淡一点，但仍旧可点（点进去是空列表 + 一行说明，比直接藏起来好理解）
  tab.classList.toggle("chan-off", !official || !official.enabled);
}

function renderConversations(force) {
    const q = state.search.trim().toLowerCase();
    // 先按通道分块（私域 / 官方商用）再搜关键词 —— 两套场景的会话不混在一起
    const items = state.conversations.filter((c) => {
      if (state.channelFilter !== "all" && (c.channel || "private") !== state.channelFilter) return false;
      return !q || c.name.toLowerCase().includes(q) || (c.preview || "").toLowerCase().includes(q);
    });

    $("convEmpty").hidden = state.conversations.length > 0;

    // 内容未变就什么都不做（SSE 会高频重复触发）
    const sig = convSignatureOf(items);
    if (!force && sig === convSignature) return;
    convSignature = sig;

    const list = $("convList");
    const seen = new Set();
    for (const c of items) {
      seen.add(c.key);
      let el = convNodes.get(c.key);
      if (!el) {
        el = createConvNode(c);
        convNodes.set(c.key, el);
      }

      updateConvNode(el, c);
      // appendChild 对已有子节点是“移动”，不会重建；循环结束自然按 items 排序
      list.appendChild(el);
    }

    for (const [key, el] of convNodes) {
      if (!seen.has(key)) {
        el.remove();
        convNodes.delete(key);
      }
    }
  }

  /* ─────────── 消息区 ─────────── */

  // 头部头像：地址未变就不重建 <img>，避免每次状态推送都重新下载
  let hdrAvatarKey = null;

  function renderHeader() {
    const c = state.byKey.get(state.activeKey);
    const connected = !!state.status?.onebot?.connected;
    const uin = state.status?.account?.uin || "-";

    if (!c) {
      $("hdrName").textContent = "未选择会话";
      $("hdrKindTag").hidden = true;
      $("hdrMeta").textContent = "从左侧选择一个会话";
      const d0 = connDisplay();
      $("hdrStatus").textContent = `${d0.short}${state.aiMode ? " · AI 自动回复开" : " · AI 自动回复关"}`;
      $("hdrDot").className = "dot " + d0.dot;
      $("hdrAvatar").style.background = "linear-gradient(135deg,#808080,#a0a0a0)";
      if (hdrAvatarKey !== null) {
        $("hdrAvatar").replaceChildren(Object.assign(document.createElement("span"), { id: "hdrAvatarText", textContent: "?" }));
        hdrAvatarKey = null;
      }
      return;
    }

    const [g1, g2] = GRADIENTS[hashIndex(c.key)];
    $("hdrName").textContent = c.name;
    const tag = $("hdrKindTag");
    tag.hidden = false;
    tag.textContent = c.kind === "Group" ? "群聊" : "私聊";
    tag.className = "kind-tag" + (c.kind === "Group" ? "" : " private");
    $("hdrMeta").textContent = `${c.kind === "Group" ? "QQ 群聊" : "QQ 私聊"} · 当前账号 QQ ${uin}`;
    const d1 = connDisplay();
    $("hdrStatus").textContent = `${d1.short}${state.aiMode ? " · AI 自动回复开" : " · AI 自动回复关"}`;
    $("hdrDot").className = "dot " + (c.thinking ? "busy" : d1.dot);

    if (hdrAvatarKey !== c.key) {
      hdrAvatarKey = c.key;
      const av = $("hdrAvatar");
      av.style.background = `linear-gradient(135deg,${g1},${g2})`;
      av.replaceChildren(Object.assign(document.createElement("span"), { id: "hdrAvatarText", textContent: c.avatarText }));
      if (c.avatarUrl) {
        const img = document.createElement("img");
        img.alt = "";
        img.addEventListener("error", () => img.remove());
        img.src = c.avatarUrl;
        av.appendChild(img);
      }
    }
  }

  function createMessageNode(m) {
    const el = document.createElement("div");
    const role = m.role.toLowerCase();
    el.className = "msg " + role;

    if (role === "self") {
      el.innerHTML = `<div class="bubble"></div><div class="msg-time">${timeText(m.time)}</div>`;
      fillBubble(el.querySelector(".bubble"), m);
      return el;
    }

    if (role === "system") {
      el.innerHTML = `<div class="bubble"></div>`;
      fillBubble(el.querySelector(".bubble"), m);
      return el;
    }

    el.innerHTML =
      (m.senderName ? `<div class="msg-sender">${esc(m.senderName)}</div>` : "") +
      `<div class="bubble"></div><div class="msg-time">${timeText(m.time)}</div>`;

    if (m.senderId) {
      const senderEl = el.querySelector(".msg-sender");
      if (senderEl) {
        const uid = document.createElement("span");
        uid.className = "uid";
        uid.title = "查看人物档案";
        uid.textContent = m.senderId;
        uid.addEventListener("click", () => showProfile(m.senderId));
        senderEl.appendChild(uid);
      }
    }

    fillBubble(el.querySelector(".bubble"), m);
    return el;
  }

  function fillBubble(bubble, m) {
    if (m.recalled) {
      // 已撤回：面板是运维视角 —— 原文看得到（方便排查），但要划掉并说清楚
      // “模型看到的是 [已撤回]”，否则操作者会以为机器人看过这条内容。
      const orig = document.createElement("span");
      orig.className = "recalled-text";
      orig.textContent = m.text || "";
      bubble.appendChild(orig);

      const tag = document.createElement("div");
      tag.className = "recall-tag";
      tag.textContent = "已撤回" + (m.recallOperator ? `（${m.recallOperator}）` : "") + " · 模型看到的是「[已撤回] 原文」";
      bubble.appendChild(tag);
    } else {
      bubble.textContent = m.text || "";
    }

    for (const u of m.images || []) {
      const img = document.createElement("img");
      img.alt = "";
      img.loading = "lazy";
      img.addEventListener("error", () => img.remove());
      img.src = u;
      bubble.appendChild(img);
    }
  }

  function isNearBottom() {
    const s = $("msgScroll");
    return s.scrollHeight - s.scrollTop - s.clientHeight < 120;
  }

  function renderMessages() {
    const box = $("msgList");
    const list = state.messages.get(state.activeKey) || [];
    $("chatEmpty").hidden = !!(state.activeKey && list.length > 0);

    const nearBottom = isNearBottom();
    const frag = document.createDocumentFragment();
    for (const m of list) {
      frag.appendChild(createMessageNode(m));
    }

    box.replaceChildren(frag);
    if (nearBottom) scrollToBottom();
  }

  /// 实时消息到达时只追加一个节点，不重建整个列表（避免滚动中收到消息就顿一下）
  function appendMessageNode(m) {
    const nearBottom = isNearBottom();
    $("msgList").appendChild(createMessageNode(m));
    $("chatEmpty").hidden = true;
    if (nearBottom) scrollToBottom();
  }

  function scrollToBottom() {
    const s = $("msgScroll");
    s.scrollTop = s.scrollHeight;
  }

  function renderThinking() {
    const c = state.byKey.get(state.activeKey);
    const t = $("thinking");
    if (c?.thinking) {
      $("thinkingText").textContent = "AI 正在思考…";
      t.hidden = false;
    } else {
      t.hidden = true;
    }
  }

  function renderAiMode() {
    const btn = $("aiToggle");
    btn.classList.toggle("on", state.aiMode);
    $("aiLabel").textContent = state.aiMode ? "AI 开" : "AI 关";
    renderHeader();
  }

  /* ─────────── 扫码登录卡片 ───────────
     为什么把二维码搬进面板：
       远程部署时 NapCat 的登录二维码只在它自己的 WebUI（另一个域名 + 另一道 Basic 认证）里看得到，
       用户打开机器人面板只看到“没有会话”，根本不知道要先扫码 —— 这是卡住最久的地方。
     实现要点：
       · 二维码由服务端从 NapCat WebUI 取（/api/qqlogin），浏览器不直连 NapCat（那条路有跨域 + 认证）
       · 每 10 秒轮询一次轻量 JSON；**只有 key（二维码指纹）变了才换 <img> 的 src**，避免扫到一半被重载
       · 账号一旦上线，卡片自己消失（用户不需要记得回来关它） */

  let loginTimer = 0;
  let loginBusy = false;

  /// 只要不是“已确认在线”，就认为需要展示登录入口（含“未知”与“面板还没连上”）
  function needsLogin() {
    return state.status?.account?.online !== true;
  }

  function renderLoginCard() {
    const card = $("loginCard");
    if (!card) return;

    const show = needsLogin() && !state.login.collapsed;
    card.hidden = !show;
    if (!show) return;

    const qr = state.login.qr;
    const img = $("loginQrImg");
    const box = $("loginQrState");

    if (qr?.ok && qr.url) {
      const want = withToken(`/api/qqlogin/qrcode.svg?k=${encodeURIComponent(qr.key || "")}`);
      if (img.dataset.src !== want) {
        img.dataset.src = want;   // 同一张二维码不重载：重载会让正在扫的图闪一下
        img.src = want;
      }
      img.hidden = false;
      box.hidden = true;
      $("loginUrlRow").hidden = false;
      $("loginUrl").textContent = qr.url;

      // 把“这张码多新”直接告诉用户：以前面板不显示年龄，
      // 一张 25 分钟前的死码看起来和刚生成的一模一样，手机扫完只会说“已过期”。
      const age = Number(qr.ageSeconds || 0);
      if (qr.stale || age > 180) {
        $("loginTitle").textContent = "二维码可能已过期";
        $("loginMsg").textContent =
          `这张码已经 ${age} 秒没换新了（NapCat 那边好像没在轮换），手机扫会提示“已过期”。` +
          "点下面的「换一张二维码」试试；若仍不换，在服务器上重启协议端：docker compose restart napcat。";
      } else {
        $("loginTitle").textContent = "QQ 未登录 · 扫码即可上线";
        $("loginMsg").textContent =
          `用手机 QQ 扫左侧二维码登录机器人账号（这张码 ${age} 秒前更新，约 2 分钟有效，会自动换新）。` +
          "登录成功后本卡片自动消失，机器人随即开始收消息。";
      }
      return;
    }

    img.hidden = true;
    box.hidden = false;
    $("loginUrlRow").hidden = true;

    if (qr && qr.configured === false) {
      $("loginTitle").textContent = "面板里取不到二维码（未配置）";
      box.textContent = "未配置";
      $("loginMsg").textContent =
        "机器人容器没有 NapCat WebUI 令牌，所以无法直接显示登录二维码。" +
        "给容器补上 QQCHAT_NAPCAT_WEBUI_TOKEN（值见 napcat/config/webui.json 的 token）后重启即可；" +
        "也可以先用 NapCat 面板扫码。";
      return;
    }

    $("loginTitle").textContent = "二维码暂时取不到";
    box.textContent = qr?.error ? "读取失败" : "正在获取二维码…";
    $("loginMsg").textContent = qr?.error
      ? `${qr.error}（面板每 10 秒自动重试）`
      : "正在向 NapCat 索取登录二维码…";
  }

  async function pollLogin(force) {
    if (loginBusy || !needsLogin()) return;
    loginBusy = true;
    try {
      state.login.qr = await api("/api/qqlogin" + (force ? "?refresh=1" : ""));
    } catch (e) {
      state.login.qr = { ok: false, configured: true, error: "读取二维码失败：" + e.message };
    } finally {
      loginBusy = false;
      $("loginRefresh").disabled = false;
      renderLoginCard();
    }
  }

  function startLoginWatch() {
    if (loginTimer) return;
    loginTimer = setInterval(() => {
      // 刻意不看 document.hidden：用户往往是“看着面板 → 拿手机扫”，
      // 切到手机/另一个窗口时标签页就变成隐藏，后台不再轮询 -> 二维码停在旧的那张，
      // 扫出来就是“二维码已过期”（线上就是这么踩的）。
      // 后台标签页的定时器本来就会被浏览器降频，代价很小。
      pollLogin(false);
    }, 10000);
  }

  /// 回到这个标签页时立刻对一次，避免展示一张“回来时已经过期”的码。
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden && needsLogin()) pollLogin(false);
  });

  /* ─────────── 连接展示态 ───────────
     关键区分：**协议端连着 ≠ QQ 账号在线**。
     登录失效/被顶号时 WebSocket 依旧连着、get_login_info 也照旧回显 UIN，
     但消息一条都进不来 —— 只显示“已连接”会把用户带进死胡同。 */
  function connDisplay() {
    const connected = !!state.status?.onebot?.connected;
    const online = state.status?.account?.online; // true / false / null(未知)

    if (!connected) {
      return { level: "off", dot: "off", short: "离线 · 未连接协议端" };
    }
    if (online === false) {
      return { level: "warn", dot: "busy", short: "账号离线 · 需重新登录" };
    }
    return { level: "on", dot: "on", short: "在线" };
  }

  function renderConn() {
    const connected = !!state.status?.onebot?.connected;
    const uin = state.status?.account?.uin || "";
    const d = connDisplay();
    $("connDot").className = "dot " + d.dot;
    $("connText").textContent = d.level === "on"
      ? `在线${uin ? " · " + uin : ""}`
      : d.short;
    $("brandSub").textContent = state.status
      ? `Web 面板 · ${state.status.agent?.model || ""}`
      : "Web 面板";

    // 账号刚上线：说一声，再把扫码卡片收起来（否则用户会一直盯着那个「没登录」的卡片）
    if (d.level === "on" && !state.login.greeted) {
      state.login.greeted = true;
      if (state.login.qr) toast("QQ 已上线，机器人开始收消息");
    } else if (d.level !== "on") {
      state.login.greeted = false;
    }

    renderLoginCard();
    renderHeader();
  }

  /* ─────────── 交互 ─────────── */

  async function selectConversation(key) {
    state.activeKey = key;
    // 手机端：切到聊天视图（桌面端没影响，见 syncMobileView 的注释）
    state.chatOpen = true;
    syncMobileView();
    renderConversations();
    renderHeader();
    renderThinking();

    try {
      const data = await api(`/api/conversations/${encodeURIComponent(key)}/messages?limit=300`);
      state.messages.set(key, data.messages || []);
      renderMessages();
      scrollToBottom();
      await api(`/api/conversations/${encodeURIComponent(key)}/read`, { method: "POST" });
      const c = state.byKey.get(key);
      if (c) c.unread = 0;
      renderConversations();
    } catch (e) {
      toast("加载消息失败：" + e.message);
    }
  }

  async function sendMessage() {
    const input = $("input");
    const text = input.value.trim();
    if (!text || !state.activeKey) return;

    const btn = $("sendBtn");
    btn.disabled = true;
    try {
      await api(`/api/conversations/${encodeURIComponent(state.activeKey)}/send`, {
        method: "POST",
        body: JSON.stringify({ text })
      });
      input.value = "";
      autoGrow();
    } catch (e) {
      toast("发送失败：" + (e.data?.error || e.message));
    } finally {
      btn.disabled = false;
      input.focus();
    }
  }

  function autoGrow() {
    const input = $("input");
    input.style.height = "auto";
    input.style.height = Math.min(input.scrollHeight, 132) + "px";
  }

  let ctxKey = null;

  function openContextMenu(x, y, key) {
    ctxKey = key;
    const menu = $("ctxMenu");
    menu.hidden = false;
    const w = menu.offsetWidth, h = menu.offsetHeight;
    menu.style.left = Math.min(x, innerWidth - w - 8) + "px";
    menu.style.top = Math.min(y, innerHeight - h - 8) + "px";
  }

  function closeContextMenu() {
    ctxKey = null;
    $("ctxMenu").hidden = true;
  }

  async function showProfile(uid) {
    try {
      const data = await api(`/api/profiles/${encodeURIComponent(uid)}`);
      $("profileTitle").textContent = `成员档案 · QQ ${uid}`;
      $("profileBody").textContent = data.summary || "（暂无档案：该成员还没有被记录发言）";
      $("profileModal").hidden = false;
    } catch (e) {
      toast("读取档案失败：" + e.message);
    }
  }

  /// 归档历史：已滑出滚动窗口的旧消息（不会被模型看到，但留档可查）
  async function showArchive(key) {
    try {
      const data = await api(`/api/archive?key=${encodeURIComponent(key)}&limit=300`);
      const name = state.byKey.get(key)?.name || key;
      $("profileTitle").textContent = `归档历史 · ${name}`;

      const list = data.messages || [];
      if (list.length === 0) {
        $("profileBody").textContent = data.error
          ? `${data.error}\n\n路径：${data.path || "-"}`
          : "（该会话尚无归档）";
      } else {
        const head = `共 ${data.totalLines} 条归档，显示最近 ${list.length} 条：\n\n`;
        $("profileBody").textContent = head + list.map((m) => {
          const d = new Date(m.t * 1000);
          const p = (n) => String(n).padStart(2, "0");
          const ts = `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
          const who = m.sender ? `${m.sender}(${m.uid || m.role})` : m.role;
          return `${ts}  ${who}\n       ${m.text}`;
        }).join("\n\n");
      }

      $("profileModal").hidden = false;
    } catch (e) {
      toast("读取归档失败：" + e.message);
    }
  }

  /* ─────────── 设置页 ─────────── */

  let settingsDirty = false;

  // 用户点了“清除密钥”：下次保存时把密钥清掉（输入框留空默认是“不改”，两者必须区分）
  let pendingApiKeyClear = false;
  /// 服务器 agent 那把 key 的“待清除”标记：留空 = 不改，清除必须显式点按钮
  let pendingAgentServerKeyClear = false;

  function markSettingsDirty() {
    // 改动序号先加：保存请求在飞的时候用户又改了东西，这次结果就不能再回填表单
    settingsEditSeq++;
    if (settingsDirty) return;
    settingsDirty = true;
    const h = $("dirtyHint");
    if (h) h.hidden = false;
  }

  function clearSettingsDirty() {
    settingsDirty = false;
    const h = $("dirtyHint");
    if (h) h.hidden = true;
  }

  /* ── 设置页分节导航（左侧分节栏，一次显示一节）──
     卡片一多：堆在一起会“一块一块”地乱，滑来滑去又累。这里按每张卡片的 h3 生成一串分节按钮：
     点一下只显示那一节（宽屏是左侧竖栏，窄屏是顶部横滑的胶囊；最后一枚“全部显示”保持单列堆叠）。
     标题是读 DOM 的 —— 以后加卡片不用改这里。 */
  let refreshSettingsNav = null;

  /*
   * 卡片里的解释文字默认折成一行，点一下展开。
   * 为什么：十来张卡片每张都堆三五行说明 —— “第一次看有用、之后全是噪音”，
   * 而且说明是不动的灰文本，堆在一起整页就是一面灰墙，真正的设置项反而被淹没。
   * 只有真被截断的才加 .fold（短说明不该出现“展开”字样，点它也没意义）。
   */
  function foldCardNotes() {
    // 页面没显示（display:none）时量出来全是 0，这时既不该折也不该打上“已处理”标记，
    // 否则等到真打开设置页时就再也不折了（踩过：线上 0/12 张卡片被折）。
    const host = $("pageSettings");
    if (!host || host.hidden) return;

    const notes = document.querySelectorAll("#pageSettings .card-head > p, #pageSettings .card > p.path-hint");
    for (const p of notes) {
      if (p.dataset.foldReady === "1") continue;

      // 这一节当前没显示（分节切换把其它卡片 hidden 了）：量不出高度，也**不能**打标记，
      // 否则切到它时已经带着 foldReady 了，就再也不折（踩过：线上 0/12 张卡片被折）。
      const card = p.closest(".card");
      if (card && card.hidden) continue;

      p.dataset.foldReady = "1";

      // 先折上再量：没折的时候 scrollHeight 与 clientHeight 都是全文高度，量不出“会不会被截”。
      // 折成一行后，clientHeight 是一行的真实高度，而 scrollHeight 仍是全文高度 —— 两者一比就知道该不该给“展开”。
      p.classList.add("fold");
      if (p.scrollHeight > p.clientHeight + 2) {
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = "fold-toggle";
        btn.textContent = "展开";
        const toggle = () => {
          const open = p.classList.toggle("open");
          btn.textContent = open ? "收起" : "展开";
        };
        btn.addEventListener("click", toggle);
        p.addEventListener("click", toggle);
        p.title = "点一下展开说明";
        p.insertAdjacentElement("afterend", btn);
      } else {
        p.classList.remove("fold");   // 本来就只有一行，不挂多余的东西
      }
    }
  }

  function initSettingsNav() {
    const nav = $("settingsNav");
    const scroller = document.querySelector("#pageSettings .settings-scroll");
    const inner = scroller && scroller.querySelector(".settings-inner");
    if (!nav || !scroller || !inner || nav.dataset.ready === "1") return;

    const cards = Array.from(inner.querySelectorAll(":scope > .card"));
    if (cards.length < 2) return;   // 只有一两张卡片就不必分节了
    nav.dataset.ready = "1";

    function titleOf(card, i) {
      const h3 = card.querySelector("h3");
      if (!h3) return `第 ${i + 1} 节`;
      // h3 里常跟一个 <span class="hint">（例如“运行日志 · 最近 200 条”）—— 导航只要主标题
      const nodes = h3.childNodes ? Array.from(h3.childNodes) : [h3];
      const text = nodes
        .filter((n) => !(n.nodeType === 1 && n.classList && n.classList.contains("hint")))
        .map((n) => n.textContent || "")
        .join("")
        .replace(/\s+/g, " ")
        .trim();
      return text || `第 ${i + 1} 节`;
    }

    const labels = cards.map((c, i) => titleOf(c, i));
    const links = [];
    let current = 0;

    /* 切到某一节：-1 = “全部显示”（单列堆到尾）。
       切换只动 hidden，不碰表单字段 —— 保存契约（每个待保存字段都在 DOM 里）不受影响。 */
    function showSection(index, keepScroll) {
      const all = index < 0;
      current = all ? -1 : Math.max(0, Math.min(cards.length - 1, index));
      cards.forEach((c, k) => { c.hidden = !all && k !== current; });
      links.forEach((b, k) => {
        const on = all ? k === links.length - 1 : k === current;
        b.classList.toggle("active", on);
        // 窄屏那排胶囊是横向滑动的：把当前项带进可视区，否则高亮了也看不见
        if (on && typeof b.scrollIntoView === "function") {
          b.scrollIntoView({ block: "nearest", inline: "nearest" });
        }
      });
      if (!keepScroll) scroller.scrollTop = 0;
      foldCardNotes();   // 卡片刚显示出来，现在才量得出“说明有没有被截断”
      try {
        history.replaceState(null, "", all ? "#sec-all" : "#sec-" + current);
      } catch (err) { /* 隐私模式下 replaceState 可能被禁：无所谓 */ }
    }

    cards.forEach((card, i) => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "section-link";
      btn.textContent = labels[i];
      btn.title = labels[i];
      btn.addEventListener("click", () => showSection(i));
      nav.appendChild(btn);
      links.push(btn);
    });

    // 最后一枚：全部显示（单列从头列到尾，方便通读或 Ctrl+F 找某个字段）
    const allBtn = document.createElement("button");
    allBtn.type = "button";
    allBtn.className = "section-link section-link-all";
    allBtn.textContent = "全部显示";
    allBtn.title = "把这十几节按单列从头列到尾";
    allBtn.addEventListener("click", () => showSection(-1));
    nav.appendChild(allBtn);
    links.push(allBtn);

    // 重新进入设置页（或从 hash 进来）时，把当前节重新亮一次
    refreshSettingsNav = () => showSection(current, true);

    // “滚动到哪一节”只在“全部显示”下才有意义（单节模式里就那一张卡片）
    function setActiveByScroll() {
      const box = scroller.getBoundingClientRect();
      let best = 0;
      let bestDist = Infinity;
      cards.forEach((card, i) => {
        const r = card.getBoundingClientRect();
        if (r.bottom < box.top + 8 || r.top > box.bottom - 8) return;   // 没在可视区里
        const dist = Math.abs(r.top - (box.top + 8));
        if (dist < bestDist) { bestDist = dist; best = i; }
      });
      links.forEach((b, k) => b.classList.toggle("active", k === best));
    }

    const raf = window.requestAnimationFrame || ((fn) => setTimeout(fn, 16));
    let scheduled = false;
    scroller.addEventListener("scroll", () => {
      if (current >= 0 || scheduled) return;   // 单节模式：不用跟着滚动改高亮
      scheduled = true;
      raf(() => {
        scheduled = false;
        setActiveByScroll();
      });
    }, { passive: true });

    // 刷新后回到同一节（hash），否则默认第一节
    const m = /^#sec-(\d+|all)$/.exec(location.hash || "");
    showSection(m ? (m[1] === "all" ? -1 : parseInt(m[1], 10)) : 0);
  }

    function fillSelect(sel, models, current, placeholder) {
    const list = Array.from(new Set([...(models || []), current].filter(Boolean)));
    sel.innerHTML = `<option value="">${placeholder}</option>` +
      list.map((m) => `<option value="${escapeHtml(m)}">${escapeHtml(m)}</option>`).join("");
    sel.value = list.includes(current) ? current : "";
  }

  /// 往 innerHTML 字符串里拼任何**来自配置/接口/桥上报**的值之前，一律过这一道。
  /// 为什么：设备名、工作目录、工具白名单都是用户/桥给的文本，一个引号或 < > 就能把那一行结构撑坏；
  /// 拼进 value="..."/placeholder="..." 这类属性时更直接（配置里出现恶意值即存储型 XSS）。
  function escapeHtml(value) {
    return String(value === null || value === undefined ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  /* ─────────── 外部设备表：拉取 / 草稿回滚 / 指定设备下拉 ─────────── */

  /// 拉服务端的设备表：作为编辑草稿 + “已保存快照”。
  /// 进设置页、点「刷新设备」、保存之后都走它 —— 以前只有点刷新才拉，
  /// 结果首次进设置页时表是空的：加了一台设备保存时被 loaded 判断拦下，保存后的刷新又把那行覆盖掉。
  async function loadAgentDevices() {
    const r = await api("/api/agent/status");
    agentDevices = (r.deviceList || []).map((d) => ({ ...d }));
    agentDevicesSaved = agentDevices.map((d) => ({ ...d }));
    agentGlobalWorkdir = r.globalWorkdir || "";
    agentDevicesLoaded = true;
    agentDevicesEdited = false;
    if (renderAgentDeviceList) renderAgentDeviceList(r);
    return r;
  }

  /// 放弃草稿 = 回到上次与服务端对齐的那份。
  function resetAgentDeviceDraft() {
    agentDevices = agentDevicesSaved.map((d) => ({ ...d }));
    agentDevicesEdited = false;
    if (renderAgentDeviceList) renderAgentDeviceList();
  }

  /// 指定设备下拉里补一项。
  /// 这里必须用 DOM API（value/textContent）而不是拼 innerHTML：设备名是外部输入。
  /// 另外它得是**顶层函数** —— loadSettings() 在外层，以前它定义在 bindUi() 内部，
  /// “指定设备 + 重新进设置页”直接 ReferenceError（loadSettings 的 catch 会把它吞成“设置页打不开”）。
  function ensureDeviceOption(name) {
    if (!name) return;
    const sel = $("setAgentDevice");
    if (!sel) return;
    const opts = sel.options ? Array.from(sel.options) : [];
    if (opts.some((o) => o.value === name)) return;
    const opt = document.createElement("option");
    opt.value = name;
    opt.textContent = name;
    sel.appendChild(opt);
  }

  /* 拉服务器 agent 接口的模型列表（GET <AgentServerBaseUrl>/models） */

  async function loadSettings() {
    state.settingsLoaded = false; // 重新加载期间先封住保存
    const data = await api("/api/settings");
    const r = data.runtime, e = data.env;

    $("setBaseUrl").value = e.modelBaseUrl || "";
    // 密钥只在界面上显示掉掩码；输入框留空 = 不改（想清空点旁边的按钮）
    $("setApiKey").value = "";
    $("setApiKey").placeholder = e.apiKeySet
      ? e.apiKeyMasked + "（已设置，留空即不修改）"
      : "还没配密钥，在这里填一个";
    $("setModel").value = e.model || "";
    $("setFastReply").checked = !!e.fastReply;
    $("setFastModel").value = e.fastModel || "";
    // 这三个值当前是从哪儿来的：面板改过就归面板，否则是容器环境变量
    $("baseUrlSrc").textContent = e.modelBaseUrlSource === "panel" ? "来自面板（保存后立即生效）" : "来自环境变量";
    $("modelSrc").textContent = e.modelSource === "panel" ? "来自面板（保存后立即生效）" : "来自环境变量";
    $("apiKeySrc").textContent = e.apiKeySource === "panel"
      ? "来自面板（存服务端密钥库，不回显）"
      : e.apiKeySource === "env" ? "来自环境变量" : "未配置";
    $("setProtocol").value = e.oneBotProtocol || "";
    $("setAddress").value = e.oneBotAddress || "";
    $("setToken").value = e.oneBotTokenMasked ? e.oneBotTokenMasked + "（来自环境变量）" : "未设置";
    $("setUin").value = e.uin || "自动识别";
    $("settingsPath").textContent = `配置文件：${data.settingsFile}`;

    $("setPersona").value = r.botPersona || "";
    $("setMaxTokens").value = r.maxTokens;
    $("setWhitelist").value = r.messageWhitelist || "";
    $("setWhitelistGroups").value = r.whitelistGroups || "";
    $("setWhitelistPrivates").value = r.whitelistPrivates || "";
    // 留空的那一边回落到旧的共用名单 —— 在提示里说清楚（不然号主以为新框填了没生效）
    const legacyBits = [];
    if (r.whitelistGroupsFromLegacy) legacyBits.push("群聊");
    if (r.whitelistPrivatesFromLegacy) legacyBits.push("私聊");
    $("whitelistLegacyHint").textContent = legacyBits.length > 0
      ? `群聊+私聊共用一份（以前只有一个框）。现在：${legacyBits.join("、")}在用这份旧名单（上面对应的新框填上就以新框为准）`
      : "群聊+私聊共用一份（以前只有一个框）。上面两个新框都填了，这份已经不起作用";
    $("setDesire").value = r.aiDesire;
    $("desireVal").textContent = r.aiDesire;
    $("setThreshold").value = r.suitabilityThreshold;
    $("threshVal").textContent = r.suitabilityThreshold;
    $("setAiMode").checked = r.aiModeEnabled;
    $("setGroupCooldown").value = r.groupCooldownSeconds;
    $("setPrivateCooldown").value = r.privateCooldownSeconds;
    $("setIdleFallback").value = r.idleFallbackSeconds;
    $("setSegmentDelay").value = r.segmentDelayMs;
    $("setMaxContext").value = r.maxContextMessages;
    $("setMaxMessages").value = r.maxMessagesPerConversation;
    $("setConcurrency").value = r.maxConcurrentReplies;
    $("setProfileLookup").value = r.profileLookupCount;
    $("setProfileLines").value = r.profileSummaryLines;
    $("setProfileChars").value = r.maxProfileChars;
    $("setEnableSummary").checked = r.enableProfileSummary;
    $("setSummaryThreshold").value = r.profileSummaryThreshold;
    $("setSummaryChars").value = r.profileSummaryMaxChars;
    $("setSummaryInterval").value = r.profileSummaryIntervalSeconds;
    $("setSplitReplies").checked = r.splitReplies;
    $("setEnableProactive").checked = r.enableProactive !== false;
    $("setProactiveCooldown").value = r.proactiveCooldownSeconds;
    $("setProactiveQuiet").value = r.proactiveQuietSeconds;
    $("setIgnoreBrackets").checked = r.ignoreBracketMessages === true;
    $("setEnableStickers").checked = r.enableStickers;
    $("setStickerMax").value = r.stickerLibraryMax;
    $("setStickerCandidates").value = r.stickerCandidates;
    $("setStickerCurate").value = r.stickerCurateIntervalSeconds;
    $("setStickerCooldown").value = r.stickerCooldownSeconds;
    $("setEnablePoke").checked = r.enablePoke !== false;
    $("setPokeCooldown").value = r.pokeCooldownSeconds;
    $("setMood").value = r.mood || "";
    $("setMoodTtl").value = r.moodTtlSeconds;
    $("moodHint").textContent = r.moodSummary ? "现在：" + r.moodSummary : "";
    $("setEnableMusic").checked = r.enableMusic !== false;
    $("setMusicSources").value = r.musicSources || "";
    $("setMusicBitrate").value = r.musicBitrate;
    $("setMusicMaxMb").value = r.musicMaxDownloadMb;
    $("setMusicAnalysisSeconds").value = r.musicMaxAnalysisSeconds;
    $("setMusicLibraryMax").value = r.musicLibraryMax;
    $("setMusicNoteTtlDays").value = r.musicNoteTtlDays;
    $("setMusicListenCooldown").value = r.musicListenCooldownSeconds;
    $("setMusicUnderstandModel").value = r.musicUnderstandModel || "";

    // 网易云登录态存在机器人库里：面板一打开就能看出“要不要重扫”
    // （没改之前它只在自建 API 容器的内存里，容器一重建就白扫了）
    if (r.neteaseCookieSet === true && $("neteaseLoginState").textContent === "未登录") {
      $("neteaseLoginState").textContent = "已有登录态（存在机器人库里，重启不丢）";
    }
    $("setMusicSendAudio").checked = r.musicSendAudioToModel !== false;
    $("audioNeteaseBase").value = r.neteaseBaseUrl || "";
    $("setMusicKeepAudio").checked = r.musicKeepAudio === true;
    $("musicHint").textContent = "网易云 Cookie：" + (r.neteaseCookieSet ? "已设置（环境变量）" : "未设置（可选）");
    $("setEnableLinkPreview").checked = r.enableLinkPreview !== false;
    $("setEnableWebSearch").checked = r.enableWebSearch === true;    $("setWebSearchUseModelSearch").checked = r.webSearchUseModelSearch !== false;
    $("setWebSearchSources").value = r.webSearchSources || "";

    // ─────────── 本机 Agent（// 命令）───────────
    $("setEnableAgentBridge").checked = r.enableAgentBridge === true;
    // 脱敏：服务端默认 true，这里只有明确 false 才关（旧配置里没这个字段时保持开）
    $("setAgentMask").checked = r.enableAgentMask !== false;
    $("setAgentPrompt").value = r.agentPrompt || "";
    state.agentPromptDefault = r.agentPromptDefault || "";
    $("setAgentAllowedUsers").value = r.agentAllowedUsers || "";
    $("setAgentPrefix").value = r.agentPrefix || "//";
    $("setAgentTimeoutSeconds").value = r.agentTimeoutSeconds;
    $("setAgentReplyMaxChars").value = r.agentReplyMaxChars;
    $("setAgentProgressSeconds").value = r.agentProgressSeconds;
    $("setAgentWorkDir").value = r.agentWorkDir || "";
    $("setEnableHostAgent").checked = r.enableHostAgent !== false;
    $("setEnableServerAgent").checked = r.enableServerAgent !== false;

    // “优先用哪边”与“指定设备”共用 AgentTarget：名字在三种模式之外 → 就是指定设备
    const target = (r.agentTarget || "auto").trim();
    const modes = ["auto", "host", "server"];
    if (modes.includes(target)) {
      $("setAgentTargetMode").value = target;
      $("setAgentDevice").value = "";
    } else {
      $("setAgentTargetMode").value = "auto";
      ensureDeviceOption(target);
      $("setAgentDevice").value = target;
    }
    $("setAgentServerTools").value = r.agentServerTools || "";
    $("setAgentServerQqActions").value = r.agentServerQqActions || "";
    // 记住上下文（默认关：每条指令单独对待）
    $("setAgentServerKeepContext").checked = !!r.agentServerKeepContext;
    // 透过 docker 操作服务器（高权限，默认关）
    $("setAgentServerDocker").checked = !!r.agentServerDocker;
    // 面板一键部署（高权限，默认关）+ 记住的产物地址
    $("setPanelDeployEnabled").checked = !!r.panelDeployEnabled;
    $("deployUrl").value = r.panelDeployUrl || "";
    // 把“实际会开哪几个”回显出来：留空 ≠ 什么都没有（是默认安全档），写错的名字会被服务端忽略，
    // 所以面板得把真正生效的那份摆出来，不然号主会以为自己写生效了。
    $("agentServerQqActionsOut").textContent = r.agentServerQqActionsEffective
      ? "现在生效：" + r.agentServerQqActionsEffective
      : "";
    $("setAgentServerMaxSteps").value = r.agentServerMaxSteps;
    $("setAgentServerWorkDir").value = r.agentServerWorkDir || "/data";
    $("setAgentServerCommandTimeoutSeconds").value = r.agentServerCommandTimeoutSeconds;
    $("setAgentServerBaseUrl").value = r.agentServerBaseUrl || "";
    $("setAgentServerModel").value = r.serverModel || "";
    // 服务器 agent 的密钥同理：只显示掩码与来源，永远不回显明文
    $("setAgentServerKey").value = "";
    $("setAgentServerKey").placeholder = r.agentServerKeySet
      ? r.agentServerKeyMasked + "（已设置，留空即不修改）"
      : "还没配（留空 = 用聊天那把）；单独填一个就只给 agent 用";
    $("agentServerKeySrc").textContent = r.agentServerKeySource === "panel"
      ? "来自面板（存服务端密钥库，不回显）"
      : r.agentServerKeySource === "env" ? "来自环境变量" : "未配置（会用聊天那把密钥）";
    pendingAgentServerKeyClear = false;
    fillSelect($("setAgentModel"), r.deviceModels || [], r.agentModel || "", "（用 pi 自己的默认）");
    $("setAgentModel").value = r.agentModel || "";

    // ─────────── 服务器健康日报（定时私聊推送）───────────
    $("setHealthReportEnabled").checked = r.healthReportEnabled === true;
    $("setHealthReportTime").value = r.healthReportTime || "18:00";
    $("setHealthReportTargets").value = r.healthReportTargets || "";
    refreshHealthReport();

    $("setWebSearchMaxResults").value = r.webSearchMaxResults;
    $("setWebSearchCooldown").value = r.webSearchCooldownSeconds;
    $("setWebSearchTimeoutSeconds").value = r.webSearchTimeoutSeconds;
    $("setEnableVoice").checked = r.enableVoice === true;
    $("setVoiceName").value = r.voiceName || "";
    $("setVoiceSpeed").value = r.voiceSpeed;
    $("setVoiceEmotion").value = r.voiceEmotion || "";
    $("setVoicePitch").value = r.voicePitch;
    $("setVoiceVol").value = r.voiceVol;
    $("setVoiceMaxChars").value = r.voiceMaxChars;
    $("setVoiceEagerness").value = r.voiceEagerness == null ? 50 : r.voiceEagerness;
    $("voiceEagernessVal").textContent = $("setVoiceEagerness").value;
    $("setTtsServiceUrl").value = r.ttsServiceUrl || "";
    // TTS 密钥：与模型密钥同规矩 —— 只回显掩码，留空 = 不改
    $("setTtsApiKey").value = "";
    $("setTtsApiKey").placeholder = r.ttsKeyConfigured
      ? `${r.ttsKeyMasked}（已设置，留空即不修改）`
      : "还没配密钥，在这里填一个（云端 TTS 必需）";
    $("setTtsProvider").value = (r.ttsProvider || "minimax").toLowerCase() === "openai" ? "openai" : "minimax";
    $("setTtsApiBase").value = r.ttsApiBase || "";
    $("setTtsModel").value = r.ttsModel || "";
    // 官方商用通道（与私域并存）；secret 不回填（它只从环境变量读，面板不接也不存）
    $("setOfficialEnabled").checked = r.officialEnabled === true;
    $("setOfficialAppId").value = r.officialAppId || "";
    // AppSecret：只回显掩码，留空 = 不改（与 TTS 密钥同规矩）
    $("setOfficialAppSecret").value = "";
    $("setOfficialAppSecret").placeholder = r.officialSecretConfigured
      ? `${r.officialSecretMasked}（已设置${r.officialSecretSource === "env" ? "，来自环境变量" : ""}，留空即不修改）`
      : "还没配 AppSecret，在这里填一个";
    $("setOfficialSandbox").checked = r.officialSandbox === true;
    $("setOfficialWhitelistGroups").value = r.officialWhitelistGroups || "";
    $("setOfficialWhitelistPrivates").value = r.officialWhitelistPrivates || "";
    $("setOfficialChatEnabled").checked = r.officialChatEnabled !== false;
    $("setPrivateChatEnabled").checked = r.privateChatEnabled !== false;
    renderOfficialConversations(r.officialConversations);
    renderChannelStatus(r.channels);
    $("setLinkPreviewTimeout").value = r.linkPreviewTimeoutSeconds;
    $("setLinkPreviewMax").value = r.linkPreviewMax;

    // NapCat 状态条（对应桌面版 InfoBar）
    const d = connDisplay();
    const bar = $("napcatBar");
    bar.className = "status-bar " + (d.level === "on" ? "" : "warn");
    $("napcatTitle").textContent = d.level === "on"
      ? "已接入 QQ"
      : d.level === "warn" ? "QQ 账号已离线" : "未连接协议端";
    $("napcatMsg").textContent = d.level === "on"
      ? `协议端在线，账号 QQ ${state.status?.account?.selfId || "-"}。消息通道正常。`
      : d.level === "warn"
        ? "协议端连着，但 QQ 账号登录已失效 —— 消息一条都收不到。回到「聊天」页，顶部卡片里直接扫码就能重新登录。"
        : `无法连接 ${e.oneBotAddress}。请确认 NapCat 容器正在运行且已开启对应的 OneBot 服务。`;
    $("napcatLoginBtn").hidden = d.level === "on";

    // 设备表也是设置的一部分：跟普通字段一样回填（拉不到就保留上一份，绝不清空）。
    // 以前只有点「刷新设备」才拉 —— 首次进来表是空的，加了一台设备保存时被 loaded 判断拦下。
    try {
      await loadAgentDevices();
    } catch (err) {
      console.warn("加载设备表失败：", err);
      resetAgentDeviceDraft(); // 回到上一份已保存快照，不保留已经放弃的改动
    }

    // 放在最后：全部回填成功才认为可保存
    state.settingsLoaded = true;
    clearSettingsDirty(); // 刚和服务端对齐，没未保存的修改
  }

  async function saveSettings() {
    // 关键防护：表单没回填完就保存 = 所有字段是空/0，会把白名单清空（→ 忽略全部消息）、
    // 把 AI 关掉、把所有数值压倒最小值 —— 相当于一键把自己的配置全毁掉。
    if (!state.settingsLoaded) {
      toast("设置还没加载成功，请刷新页面后重试");
      return;
    }

    const payload = {
      // 模型接口（面板可改；留空 = 回退环境变量）
      modelBaseUrl: $("setBaseUrl").value.trim(),
      model: $("setModel").value.trim(),
      fastReply: $("setFastReply").checked,
      fastModel: $("setFastModel").value.trim(),
      botPersona: $("setPersona").value,
      messageWhitelist: $("setWhitelist").value,
      whitelistGroups: $("setWhitelistGroups").value,
      whitelistPrivates: $("setWhitelistPrivates").value,
      aiDesire: Number($("setDesire").value),
      suitabilityThreshold: Number($("setThreshold").value),
      aiModeEnabled: $("setAiMode").checked,
      maxTokens: Number($("setMaxTokens").value),
      groupCooldownSeconds: Number($("setGroupCooldown").value),
      privateCooldownSeconds: Number($("setPrivateCooldown").value),
      idleFallbackSeconds: Number($("setIdleFallback").value),
      splitReplies: $("setSplitReplies").checked,
      enableProactive: $("setEnableProactive").checked,
      proactiveCooldownSeconds: Number($("setProactiveCooldown").value),
      proactiveQuietSeconds: Number($("setProactiveQuiet").value),
      ignoreBracketMessages: $("setIgnoreBrackets").checked,
      segmentDelayMs: Number($("setSegmentDelay").value),
      maxContextMessages: Number($("setMaxContext").value),
      maxMessagesPerConversation: Number($("setMaxMessages").value),
      maxConcurrentReplies: Number($("setConcurrency").value),
      profileLookupCount: Number($("setProfileLookup").value),
      profileSummaryLines: Number($("setProfileLines").value),
      maxProfileChars: Number($("setProfileChars").value),
      enableProfileSummary: $("setEnableSummary").checked,
      profileSummaryThreshold: Number($("setSummaryThreshold").value),
      profileSummaryMaxChars: Number($("setSummaryChars").value),
      profileSummaryIntervalSeconds: Number($("setSummaryInterval").value),
      enableStickers: $("setEnableStickers").checked,
      stickerLibraryMax: Number($("setStickerMax").value),
      stickerCandidates: Number($("setStickerCandidates").value),
      stickerCurateIntervalSeconds: Number($("setStickerCurate").value),
      stickerCooldownSeconds: Number($("setStickerCooldown").value),
      enablePoke: $("setEnablePoke").checked,
      pokeCooldownSeconds: Number($("setPokeCooldown").value),
      moodTtlSeconds: Number($("setMoodTtl").value),
      mood: $("setMood").value.trim(),
      enableMusic: $("setEnableMusic").checked,
      musicSources: $("setMusicSources").value.trim(),
      musicBitrate: Number($("setMusicBitrate").value),
      musicMaxDownloadMb: Number($("setMusicMaxMb").value),
      musicMaxAnalysisSeconds: Number($("setMusicAnalysisSeconds").value),
      musicLibraryMax: Number($("setMusicLibraryMax").value),
      musicNoteTtlDays: Number($("setMusicNoteTtlDays").value),
      musicListenCooldownSeconds: Number($("setMusicListenCooldown").value),
      musicUnderstandModel: $("setMusicUnderstandModel").value.trim(),
      musicSendAudioToModel: $("setMusicSendAudio").checked,
      neteaseBaseUrl: $("audioNeteaseBase").value.trim(),
      musicKeepAudio: $("setMusicKeepAudio").checked,
      enableLinkPreview: $("setEnableLinkPreview").checked,
      enableWebSearch: $("setEnableWebSearch").checked,
      enableAgentBridge: $("setEnableAgentBridge").checked,
      enableAgentMask: $("setAgentMask").checked,
      agentPrompt: $("setAgentPrompt").value,
      healthReportEnabled: $("setHealthReportEnabled").checked,
      healthReportTime: $("setHealthReportTime").value.trim() || "18:00",
      healthReportTargets: $("setHealthReportTargets").value.trim(),
      agentAllowedUsers: $("setAgentAllowedUsers").value.trim(),
      agentPrefix: $("setAgentPrefix").value.trim() || "//",
      agentTimeoutSeconds: Number($("setAgentTimeoutSeconds").value),
      agentReplyMaxChars: Number($("setAgentReplyMaxChars").value),
      agentProgressSeconds: Number($("setAgentProgressSeconds").value),
      agentWorkDir: $("setAgentWorkDir").value.trim(),
      agentTarget: $("setAgentDevice").value.trim() || $("setAgentTargetMode").value,
      enableHostAgent: $("setEnableHostAgent").checked,
      enableServerAgent: $("setEnableServerAgent").checked,
      agentServerTools: $("setAgentServerTools").value.trim(),
      agentServerQqActions: $("setAgentServerQqActions").value.trim(),
      agentServerKeepContext: $("setAgentServerKeepContext").checked,
      agentServerDocker: $("setAgentServerDocker").checked,
      panelDeployEnabled: $("setPanelDeployEnabled").checked,
      panelDeployUrl: $("deployUrl").value.trim(),
      agentServerMaxSteps: Number($("setAgentServerMaxSteps").value),
      agentServerWorkDir: $("setAgentServerWorkDir").value.trim() || "/data",
      agentServerCommandTimeoutSeconds: Number($("setAgentServerCommandTimeoutSeconds").value),
      agentServerModel: $("setAgentServerModel").value.trim(),
      agentServerBaseUrl: $("setAgentServerBaseUrl").value.trim(),
      agentModel: $("setAgentModel").value,
      webSearchUseModelSearch: $("setWebSearchUseModelSearch").checked,
      webSearchSources: $("setWebSearchSources").value.trim(),
      webSearchMaxResults: Number($("setWebSearchMaxResults").value),
      webSearchCooldownSeconds: Number($("setWebSearchCooldown").value),
      webSearchTimeoutSeconds: Number($("setWebSearchTimeoutSeconds").value),
      enableVoice: $("setEnableVoice").checked,
      voiceName: $("setVoiceName").value.trim(),
      voiceSpeed: Number($("setVoiceSpeed").value),
    voiceEmotion: $("setVoiceEmotion").value.trim(),
    voicePitch: Number($("setVoicePitch").value),
    voiceVol: Number($("setVoiceVol").value),
      voiceMaxChars: Number($("setVoiceMaxChars").value),
      voiceEagerness: Number($("setVoiceEagerness").value),
      ttsServiceUrl: $("setTtsServiceUrl").value.trim(),
      // 留空 = **不改**（服务端把空字符串当“保持原样”）。
      // 早期版本把空当“清空”，真实踩到：改个白名单就把 TTS key 抹了 → 语音全部失败（retcode 1200）。
      // TTS 密钥：字段照发（与表单一一对应），但**空 = 不改**（服务端按这个口径处理）
      ttsApiKey: $("setTtsApiKey").value.trim(),
      ttsProvider: $("setTtsProvider").value,
      ttsApiBase: $("setTtsApiBase").value.trim(),
      ttsModel: $("setTtsModel").value.trim(),
      // 官方商用通道（QQ 开放平台）
      officialEnabled: $("setOfficialEnabled").checked,
      officialAppId: $("setOfficialAppId").value.trim(),
      // 空 = 不改（服务端按这个口径处理，别把它当清空）
      officialAppSecret: $("setOfficialAppSecret").value.trim(),
      officialSandbox: $("setOfficialSandbox").checked,
      officialWhitelistGroups: $("setOfficialWhitelistGroups").value.trim(),
      officialWhitelistPrivates: $("setOfficialWhitelistPrivates").value.trim(),
      officialChatEnabled: $("setOfficialChatEnabled").checked,
      privateChatEnabled: $("setPrivateChatEnabled").checked,
      linkPreviewTimeoutSeconds: Number($("setLinkPreviewTimeout").value),
      linkPreviewMax: Number($("setLinkPreviewMax").value)
    };

    // 外部设备表：以前这个字段**根本没进保存请求** —— 面板里改了某台设备的模型/目录/工具/超时/启用，
    // 点保存也白改（服务器那边还是旧值；号主报过“状态显示的工作目录被固定了”）。
    // 只回写配置字段（online/cwd/pi/models 是服务器算出来的，不要捎回去）。
    // 且**只有真的拉过一份设备表才回写**：否则一次「加载失败 + 保存」就会把设备配置清空。
    if (agentDevicesLoaded) {
      payload.agentDevices = JSON.stringify(agentDevices.map((d) => ({
        name: d.name,
        enable: !!d.enable,
        model: d.model || "",
        workdir: d.workdir || "",
        tools: d.tools || "",
        timeoutSec: Number(d.timeoutSec) || 0
      })));
    } else if (agentDevicesEdited) {
      // 设备表没拉到、但用户在面板里加/删/改过：
      //   ① 只发这一份 → 服务端是**整表替换**，别的设备配置会被一起删掉；
      //   ② 直接丢掉 → 就是以前那个“新加的设备保存后又自己消失”的毛病。
      // 所以什么都不发，但保存提示里要说清“设备改动这次没保存”（不能无声无息）。
      deviceTableSkipped = true;
    }

    // 密钥单独处理：输入框留空 = 不改（否则每次保存都会把已存的密钥抹掉）；
    // 想清除要点“清除密钥”按钮（那里有二次确认）。
    const typedKey = $("setApiKey").value.trim();
    if (typedKey) payload.apiKey = typedKey;
    else if (pendingApiKeyClear) payload.apiKey = "";

    // 服务器 agent 的密钥：同样的规矩（留空 = 不改；清除要显式点按钮，二次确认在那边）
    const typedAgentKey = $("setAgentServerKey").value.trim();
    if (typedAgentKey) payload.agentServerKey = typedAgentKey;
    else if (pendingAgentServerKeyClear) payload.agentServerKey = "";

    const btn = $("saveBtn");
    btn.disabled = true;
    const deviceTableSkippedHere = deviceTableSkipped;
    deviceTableSkipped = false;
    // 快照一下改动序号：请求在飞时用户又改了东西的话，这次返回的刷新结果已经过时
    const seqAtSend = settingsEditSeq;
    try {
      const data = await api("/api/settings", { method: "POST", body: JSON.stringify(payload) });
      state.aiMode = data.runtime.aiModeEnabled;
      renderAiMode();
      // 设备表按服务器实际状态重画：改完目录/模型后，那行“目录 …”提示与输入框都跟着新值走。
      // 单独兜住：刷新失败不影响“保存成功”这个事实（否则会把刷新的锅扣在保存上）。
      // 但请求期间用户又改了东西时**不能**刷：那等于用旧结果盖掉他刚敲的字。
      const lateEdits = settingsEditSeq !== seqAtSend;
      if (!lateEdits) {
        try {
          if (afterSettingsSaved) await afterSettingsSaved();
        } catch (e) {
          console.warn("保存后刷新设备表失败：", e);
        }
      }
      const bar = $("saveBar");
      bar.hidden = false;
      $("saveBarText").textContent = "设置已保存并立即生效（会写入 settings.json，重启不回滚）" +
        (deviceTableSkippedHere ? "；设备表这次没加载成功，设备相关改动**没有**保存 —— 点「刷新设备」后再保存一次" : "") +
        (lateEdits ? "；保存期间你又有新的修改，那些还没保存" : "");
      // 密钥保存/清除后清空输入框（不回显），并把“待清除”标记归位
      $("setApiKey").value = "";
      pendingApiKeyClear = false;
      $("setAgentServerKey").value = "";
      pendingAgentServerKeyClear = false;
      // 保存期间还有新编辑 → 未保存标记要留着（清掉就等于告诉他“已经存下了”）
      if (!lateEdits) clearSettingsDirty();
      clearTimeout(bar._t);
      bar._t = setTimeout(() => { bar.hidden = true; }, 4000);
    } catch (err) {
      toast("保存失败：" + err.message);
    } finally {
      btn.disabled = false;
    }
  }

  /* ─────────── 日志 ─────────── */
  /// 日志很长时不可能一直拖滚动条：顶部 / 底部两个按钮一键跳（号主要求）。
  /// 注意：跳到最底后如果又来了新日志，renderLogs() 会自己跟上（它在底部附近才自动滚）。
  function bindLogScrollButtons() {
    const top = $("logTopBtn");
    const bottom = $("logBottomBtn");
    if (top) {
      top.addEventListener("click", () => {
        const box = $("logBox");
        if (box) box.scrollTop = 0;   // 顶部：最老的那几行
      });
    }

    if (bottom) {
      bottom.addEventListener("click", () => {
        const box = $("logBox");
        if (box) box.scrollTop = box.scrollHeight;   // 底部：最新的那几行
      });
    }
  }
  /* ─────────── 服务器健康日报（定时私聊推送）─────────── */
  /* “预览”只生成不发（不碰 QQ）；“现在发一条”真发（当场验收用）。
     这两个函数必须是**顶层**的：loadSettings() 里要调 refreshHealthReport()，
     陷在别的函数体里就会抛 ReferenceError，而 loadSettings 的 catch 会把它吞成
     “保存按钮点了没反应”（面板上最难查的一类坑，handoff §31.9 记过一次）。 */
  async function runHealthReport(mode) {
    const out = $("healthReportOut");
    out.textContent = mode === "send" ? "正在生成并发送…" : "正在生成…";
    try {
      if (mode === "send") {
        await saveSettings();   // 先用当前设置（改了时刻/收件人不必先手动保存一次）
      }

      const r = await api("/api/health-report", {
        method: "POST",
        body: JSON.stringify({ mode })
      });
      out.textContent = (r && r.text) || "（空）";
      if (mode === "send") {
        toast(r && r.ok ? "已私聊发出" : "发送失败：" + ((r && r.error) || "未知"));
      }

      refreshHealthReport();
    } catch (err) {
      out.textContent = "失败：" + ((err.data && err.data.error) || err.message);
    }
  }

  /* 下次推送 / 上次结果。服务端返回的 nextRunAt 带 +08:00 偏移，
     这里直接按浏览器本地时区渲染 —— 号主在国内，看到的就是北京时间。 */
  function healthReportMomentText(iso) {
    if (!iso) return "—";
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return "—";
    const now = new Date();
    const day = (x) => `${x.getFullYear()}-${x.getMonth() + 1}-${x.getDate()}`;
    const tomorrow = new Date(now.getTime() + 86400000);
    const prefix = day(d) === day(now) ? "今天 " : day(d) === day(tomorrow) ? "明天 " : `${d.getMonth() + 1}-${d.getDate()} `;
    return prefix + `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
  }

  async function refreshHealthReport() {
    const hint = $("healthReportHint");
    if (!hint) return;
    try {
      const r = await api("/api/health-report");
      const bits = [];
      if (r.enabled) {
        bits.push(r.nextRunAt ? `下次推送：${healthReportMomentText(r.nextRunAt)}（北京时间）` : "已启用，但还没填收件人 → 不会发");
      } else {
        bits.push("未启用");
      }

      if (r.lastSentAt) bits.push(`上次发出：${healthReportMomentText(r.lastSentAt)}`);
      if (r.lastError) bits.push(`上次失败：${r.lastError}`);
      hint.textContent = bits.join("｜");
    } catch (err) {
      console.error("读取健康日报状态失败", err);   // 静默失败最难查
      hint.textContent = "状态读取失败：" + err.message;
    }
  }

  /// 面板日志 = 服务端日志的尾部（`/api/logs`）+ 之后的实时流（SSE）。
  /// 以前只存浏览器内存：**一刷新页面就全没了**（号主反馈），现在首屏先把历史拉回来。
  async function loadLogs() {
    try {
      const data = await api("/api/logs?limit=300");
      const seen = new Set();
      const merged = [];
      for (const l of (data && data.lines) || []) {
        const item = { t: l.time || Date.now(), text: l.text || "" };
        seen.add(item.t + "|" + item.text);
        merged.push(item);
      }
      // 首屏拉取期间 SSE 可能已经推来几行：保留它们（去重后接在后面，再按时间排一次）
      for (const l of state.logs) {
        const key = l.t + "|" + l.text;
        if (seen.has(key)) continue;
        seen.add(key);
        merged.push(l);
      }
      merged.sort((a, b) => a.t - b.t);
      state.logs = merged.slice(-600);
      renderLogs(true);
    } catch {
      // 拉不到就只显实时流（不影响其它功能）
    }
  }

  function pushLog(text, time) {
    const t = time || Date.now();
    // 去重：历史回填与实时流可能在交界处重复一行（同一毫秒 + 同一文本）
    const last = state.logs[state.logs.length - 1];
    if (last && last.t === t && last.text === text) return;
    state.logs.push({ t, text });
    if (state.logs.length > 600) state.logs.splice(0, state.logs.length - 600);
    renderLogs();
  }

  function renderLogs(forceBottom) {
    const box = $("logBox");
    if (!box) return;
    const nearBottom = forceBottom || box.scrollHeight - box.scrollTop - box.clientHeight < 60;
    box.innerHTML = state.logs.map((l) => {
      const d = new Date(l.t);
      const p = (n) => String(n).padStart(2, "0");
      return `<span class="lt">${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}</span> ${esc(l.text)}`;
    }).join("\n");
    if (nearBottom) box.scrollTop = box.scrollHeight;
  }

  /* ─────────── 实时事件（SSE） ─────────── */

  function applyState(data) {
    state.status = data.status;
    state.aiMode = data.aiMode;
    state.conversations = data.conversations || [];
    state.byKey = new Map(state.conversations.map((c) => [c.key, c]));

    if (state.activeKey && !state.byKey.has(state.activeKey)) {
      state.activeKey = null;
      state.messages.clear();
      renderMessages();
    }

    renderConversations();
    renderConn();
    renderAiMode();
    renderThinking();
  }

  function connectEvents() {
    const es = new EventSource(withToken("/api/events"));

    es.addEventListener("state", (e) => applyState(JSON.parse(e.data)));

    es.addEventListener("conversations", (e) => {
      const data = JSON.parse(e.data);
      state.conversations = data.conversations || [];
      state.byKey = new Map(state.conversations.map((c) => [c.key, c]));
      renderConversations();
      renderThinking();
    });

    es.addEventListener("message", (e) => {
      const { key, message } = JSON.parse(e.data);
      const list = state.messages.get(key) || [];
      // 去重：SSE 与 REST 可能重叠
      if (list.some((m) => m.seq === message.seq)) return;
      list.push(message);
      state.messages.set(key, list);

      if (key === state.activeKey) {
        appendMessageNode(message);
        fetch(withToken(`/api/conversations/${encodeURIComponent(key)}/read`), { method: "POST", headers: authHeaders() }).catch(() => {});
      }
      renderConversations();
    });

    es.addEventListener("thinking", (e) => {
      const { key, thinking } = JSON.parse(e.data);
      const c = state.byKey.get(key);
      if (c) c.thinking = thinking;
      renderConversations();
      if (key === state.activeKey) renderThinking();
    });

    es.addEventListener("log", (e) => {
      const l = JSON.parse(e.data);
      pushLog(l.text, l.time);
    });

    es.onerror = () => {
      $("connText").textContent = "面板连接中断，重连中…";
      $("connDot").className = "dot busy";
    };
  }

  /* ─────────── 启动 ─────────── */

  /// 切页。离开设置页且有未保存的修改时会先问一句 ——
  /// 否则用户会以为“改了自动复原”：其实是从未保存，回页时又被服务端值回填了。
  function showPage(page) {
    if (page !== "settings" && !$("pageSettings").hidden && settingsDirty &&
        !confirm("设置页还有未保存的修改，确定离开吗？")) {
      return false;
    }

    for (const b of document.querySelectorAll(".navitem, .mtab")) {
      if (b.dataset.page === page) b.classList.add("active"); else b.classList.remove("active");
    }

    $("pageChat").hidden = page !== "chat";
    $("pageSettings").hidden = page !== "settings";

    if (page === "settings") {
      loadSettings().catch((e) => {
      // 不能只是 toast：设置回填一半失败时，症状是“保存按钮点了没反应”，
      // 事后光看界面根本看不出原因（调试时踩过）—— 控制台一定要有原始异常。
      console.error("loadSettings failed", e);
      toast("加载设置失败：" + e.message);
    });
      // 页面刚显示出来时元素才有尺寸，分节导航的高亮要等这一刻才能算准
      if (refreshSettingsNav) setTimeout(refreshSettingsNav, 0);
    }
    if (page === "chat" && needsLogin()) pollLogin(false);
    if (page === "settings") {
      // 去设置页就把聊天视图收起来：回来时看到的是列表，而不是停在某个会话上
      state.chatOpen = false;
      syncMobileView();
    }
    return true;
  }

  function bindUi() {
    bindLogScrollButtons();

    // 导航（桌面：左侧 rail；手机：底部标签栏 —— 共用同一套 data-page）
    for (const btn of document.querySelectorAll(".navitem, .mtab")) {
      btn.addEventListener("click", () => showPage(btn.dataset.page));
    }

    // 手机端：从聊天返回会话列表
    $("chatBack").addEventListener("click", () => {
      state.chatOpen = false;
      syncMobileView();
      renderConversations();
    });

    // 扫码卡片：换一张 / 收起
    $("loginRefresh").addEventListener("click", () => {
      $("loginRefresh").disabled = true;
      state.login.qr = null;
      renderLoginCard();
      pollLogin(true);
    });
    $("loginCollapse").addEventListener("click", () => {
      state.login.collapsed = true;
      renderLoginCard();
    });
    // 设置页的 NapCat 状态条 → 一键回到聊天页看二维码
    $("napcatLoginBtn").addEventListener("click", () => {
      state.login.collapsed = false;
      if (showPage("chat")) pollLogin(true);
      renderLoginCard();
    });

    // 会话列表搜索：用 rAF 节流，避免每次按键都重排整个列表
    let searchRaf = 0;
    $("search").addEventListener("input", (e) => {
      const value = e.target.value;
      cancelAnimationFrame(searchRaf);
      searchRaf = requestAnimationFrame(() => {
        state.search = value;
        renderConversations();
      });
    });

    // 通道分块切换（全部 / 私域 / 官方商用）：只改筛选条件，不重拉数据 ——
    // 会话数据里本来就带 channel，切板块就是换个过滤。
    $("chanTabs").addEventListener("click", (e) => {
      const btn = e.target.closest(".chan-tab");
      if (!btn) return;
      state.channelFilter = btn.dataset.chan || "all";
      for (const b of $("chanTabs").querySelectorAll(".chan-tab")) {
        b.classList.toggle("active", b === btn);
      }
      renderConversations(true);
    });

    // 一键重启：把“重启才生效”的设置落地（官方通道凭据、容器级改动…），不用开 SSH。
    // 进程会退出、容器自己回来 —— 所以点完先轮询 /healthz，回来了再刷一遍状态。
    $("restartBtn").addEventListener("click", async () => {
      const hint = $("restartHint");
      if (!confirm("重启机器人？\n\n约 5~15 秒不可用（群里的消息会等它回来后一起处理），重启后设置才生效。")) return;
      $("restartBtn").disabled = true;
      hint.textContent = "正在重启…";
      try {
        await api("/api/restart", { method: "POST" });
      } catch (e) {
        // 请求可能在进程退出的瞬间断掉，这不算失败：下面靠 /healthz 判定
      }
      const t0 = Date.now();
      const tick = setInterval(() => {
        hint.textContent = `正在重启…（已等 ${Math.round((Date.now() - t0) / 1000)} 秒）`;
      }, 1000);
      for (let i = 0; i < 60; i++) {
        await new Promise((r) => setTimeout(r, 1000));
        try {
          const r = await fetch(`/healthz?_=${Date.now()}`, { cache: "no-store" });
          if (r.ok) {
            clearInterval(tick);
            hint.textContent = `已回来（约 ${Math.round((Date.now() - t0) / 1000)} 秒）。`;
            $("restartBtn").disabled = false;
            // 服务端重启后设置/连接状态都变了，重拉一次（否则界面还停在旧状态）
            try { await loadSettings(); } catch (e) { /* 忽略 */ }
            return;
          }
        } catch (e) { /* 还没起来，接着等 */ }
      }
      clearInterval(tick);
      hint.textContent = "等了 60 秒还没回来 —— 去服务器看 docker logs qqchat-bot（容器自带重启策略，通常会自己拉起）。";
      $("restartBtn").disabled = false;
    });

    $("aiToggle").addEventListener("click", async () => {
      try {
        const data = await api("/api/ai-mode", {
          method: "POST",
          body: JSON.stringify({ enabled: !state.aiMode })
        });
        state.aiMode = data.aiMode;
        renderAiMode();
        toast(state.aiMode ? "AI 自动回复已开启" : "AI 自动回复已关闭");
      } catch (e) {
        toast("切换失败：" + e.message);
      }
    });

    const input = $("input");
    input.addEventListener("input", autoGrow);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        sendMessage();
      }
    });
    $("sendBtn").addEventListener("click", sendMessage);

    // 主题
    const saved = localStorage.getItem("qqchat.theme") || "system";
    $("setTheme").value = saved;
    applyTheme(saved);
    $("setTheme").addEventListener("change", (e) => {
      localStorage.setItem("qqchat.theme", e.target.value);
      applyTheme(e.target.value);
    });

    $("setDesire").addEventListener("input", (e) => { $("desireVal").textContent = e.target.value; });
    $("setVoiceEagerness").addEventListener("input", (e) => { $("voiceEagernessVal").textContent = e.target.value; });
    $("setThreshold").addEventListener("input", (e) => { $("threshVal").textContent = e.target.value; });
    $("saveBtn").addEventListener("click", saveSettings);

    // 清除密钥：必须先确认（密钥没了机器人就发不出话，不是小事）
    $("clearApiKey").addEventListener("click", () => {
      if (!state.settingsLoaded) { toast("设置还没加载成功，请刷新页面后重试"); return; }
      pendingApiKeyClear = true;
      $("setApiKey").value = "";
      $("apiKeySrc").textContent = "待清除（点保存后生效）";
      markSettingsDirty();
      toast("点“保存设置”后生效（会回退到环境变量里的密钥）");
    });

    // 清除服务器 agent 的密钥：同样要二次确认（清掉就回退到聊天那把 key）
    $("clearAgentServerKey").addEventListener("click", () => {
      if (!state.settingsLoaded) { toast("设置还没加载成功，请刷新页面后重试"); return; }
      pendingAgentServerKeyClear = true;
      $("setAgentServerKey").value = "";
      $("agentServerKeySrc").textContent = "待清除（点保存后生效）";
      markSettingsDirty();
      toast("点“保存设置”后生效（会回退到环境变量 / 聊天那把密钥）");
    });

    // 任何改动都标记为“未保存”（主题除外：它只存 localStorage，不进保存请求）
    const dirtyOnEdit = (ev) => { if (!ev.target || ev.target.id !== "setTheme") markSettingsDirty(); };
    $("pageSettings").addEventListener("input", dirtyOnEdit);
    $("pageSettings").addEventListener("change", dirtyOnEdit);

    // 右键菜单
    document.addEventListener("click", closeContextMenu);
    // 右键菜单：滚动时关闭。必须 passive + 先判状态，否则每次滚动都写 DOM 造成样式失效
    document.addEventListener("scroll", () => {
      if (ctxKey !== null) closeContextMenu();
    }, { capture: true, passive: true });
    $("ctxMenu").addEventListener("click", async (e) => {
      const act = e.target.dataset.act;
      if (!act || !ctxKey) return;
      const key = ctxKey;
      closeContextMenu();

      if (act === "read") {
        await fetch(withToken(`/api/conversations/${encodeURIComponent(key)}/read`), { method: "POST", headers: authHeaders() }).catch(() => {});
      } else if (act === "delete") {
        if (!confirm("确定删除这个会话？会话记录会一并从磁盘移除。")) return;
        await fetch(withToken(`/api/conversations/${encodeURIComponent(key)}/delete`), { method: "POST", headers: authHeaders() }).catch(() => {});
        if (state.activeKey === key) {
          state.activeKey = null;
          state.messages.delete(key);
          renderMessages();
        }
        toast("会话已删除");
      } else if (act === "profile") {
        const uid = key.split(":")[1];
        if (key.startsWith("private:")) showProfile(uid);
        else toast("群会话没有单一成员档案，点击消息里的 QQ 号查看具体成员");
      } else if (act === "archive") {
        showArchive(key);
      }
    });

    $("profileClose").addEventListener("click", () => { $("profileModal").hidden = true; });
    $("profileModal").addEventListener("click", (e) => {
      if (e.target === $("profileModal")) $("profileModal").hidden = true;
    });

    // 表情包库
    $("openStickerLib").addEventListener("click", () => { openStickerLib(); });
    $("stickerClose").addEventListener("click", () => { $("stickerModal").hidden = true; });
    $("stickerModal").addEventListener("click", (e) => {
      if (e.target === $("stickerModal")) $("stickerModal").hidden = true;
    });
    $("stickerCurate").addEventListener("click", requestStickerCurate);
    $("stickerImport").addEventListener("click", requestStickerImport);
    $("stickerCurateNow").addEventListener("click", requestStickerCurate);
    $("stickerImportAlbum").addEventListener("click", requestStickerImport);

    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape") {
        closeContextMenu();
        $("profileModal").hidden = true;
        $("stickerModal").hidden = true;
      }
    });
  }

  function applyTheme(theme) {
    const dark = theme === "dark" ||
      (theme === "system" && matchMedia("(prefers-color-scheme: dark)").matches);
    document.documentElement.dataset.theme = dark ? "dark" : "light";
  }

  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    if ((localStorage.getItem("qqchat.theme") || "system") === "system") applyTheme("system");
  });

  // ── 音频源接入弹窗：自己编辑/添加音源接口，并可以当场试听一首验证 ──
  // 不另存一份配置：弹窗里的字段就是设置页对应字段的“放大版”，
  // 关弹窗/试听前写回去，保存与回填契约始终只有一处（探针在盯这个）。
  function openAudioSources() {
    $("audioSources").value = $("setMusicSources").value;
    $("audioModel").value = $("setMusicUnderstandModel").value;
    $("audioTestOut").textContent = "";
    $("audioModal").hidden = false;
  }

  function writeBackAudioSources() {
    $("setMusicSources").value = $("audioSources").value;
    $("setMusicUnderstandModel").value = $("audioModel").value;
  }

  function bindAudioSources() {
    $("openAudioSources").addEventListener("click", openAudioSources);
    $("audioClose").addEventListener("click", () => { writeBackAudioSources(); $("audioModal").hidden = true; });

    // 一键填预设：公开音源会挂、会改参数，能随手改才是关键
    const presets = [
      ["audioPresetMeting", "meting|https://api.qijieya.cn/meting/?type=url&id={id}&br={br}"],
      ["audioPresetGdstudio", "gdstudio|https://music-api.gdstudio.xyz/api.php?types=url&source=netease&id={id}&br={br}&s={crc32}"],
      ["audioPresetDirect", "direct|https://example.com/song/{id}.mp3"]
    ];
    presets.forEach(([id, line]) => {
      $(id).addEventListener("click", () => {
        const box = $("audioSources");
        box.value = (box.value.trim() ? box.value.trim() + "\n" : "") + line;
      });
    });
    $("audioClear").addEventListener("click", () => { $("audioSources").value = ""; });

    $("audioTestGo").addEventListener("click", async () => {
      const song = $("audioTestSong").value.trim();
      if (!song) { toast("先填个歌名"); return; }
      const out = $("audioTestOut");
      out.textContent = "正在试听…（搜歌 → 歌词 → 音频 → 波形分析 → 模型听感，可能要十几秒）";
      try {
        writeBackAudioSources();      // 先把弹窗里的改动写回设置，否则测的还是旧配置
        await saveSettings();
        const r = await api("/api/music/test", { method: "POST", body: JSON.stringify({ song }) });
        out.textContent = r && r.ok ? r.note : "没听到：" + ((r && r.note) || "（无返回）");
      } catch (err) {
        out.textContent = "试听失败：" + err.message;
      }
    });

    $("musicTestNow").addEventListener("click", () => { openAudioSources(); });

    /* ─────────── 语音（TTS）───────────
       两条入口：
         • 「试听一句」把文本交给机器人转发的 TTS，拿回 wav 字节直接在这里播 ——
           当场就能确认“服务通不通、音色像不像、语速合不合适”；
         • 「检查 TTS 服务」只看对方 /health 有没有应答（排障用，不合成）。
       服务地址用的是**已保存**的设置（面板不做“拿任意 URL 去访问”的入口）。 */
    $("voiceTestGo").addEventListener("click", async () => {
      const out = $("voiceTestHint");
      const text = $("voiceTestText").value.trim() || "你好呀，我是昭，这是一条语音测试。";
      out.textContent = "正在合成…（第一次要等几秒）";
      try {
        const res = await fetch(withToken("/api/voice/test"), {
          method: "POST",
          headers: authHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify({
            text,
            voice: $("setVoiceName").value.trim(),
            speed: Number($("setVoiceSpeed").value),
      emotion: $("setVoiceEmotion").value.trim(),
      pitch: Number($("setVoicePitch").value),
      vol: Number($("setVoiceVol").value) / 100
          })
        });
        if (!res.ok) {
          const data = await res.json().catch(() => null);
          out.textContent = "合成失败：" + ((data && data.error) || ("HTTP " + res.status));
          return;
        }
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const kb = Math.round(blob.size / 1024);

        // 合成好的音频挂到页面上（不只是自动播一次）：
        // 浏览器不让自动播放（或没声卡/无头环境）时，还能手动点那个播放器听。
        const holder = $("voiceTestAudio");
        holder.innerHTML = "";
        const audio = document.createElement("audio");
        audio.controls = true;
        audio.src = url;
        audio.style.height = "32px";
        audio.style.marginTop = "6px";
        audio.addEventListener("ended", () => URL.revokeObjectURL(url));
        holder.appendChild(audio);

        try {
          await audio.play();
          out.textContent = `合成成功（${kb} KB）—— 正在播放…`;
        } catch (playErr) {
          out.textContent = `合成成功（${kb} KB）—— 浏览器没让自动播放，点开下面的播放器听：`;
        }
      } catch (err) {
        out.textContent = "试听失败：" + err.message;
      }
    });

    /* ─────────── 联网搜索 ───────────
       一个入口两种用法：填搜索词就是搜，填 http(s) 网址就是读那页正文。
       为什么让面板直接跑：搜索能不能用跟服务器 IP、网关支不支持 google_search 强相关，
       当场跑一次比在群里碰运气强。 */
    /* ─────────── 本机 Agent（// 命令）───────────
       为什么把“送到本机跑一下”放面板里：桥通不通、pi 能不能跑、令牌对不对 ——
       这三件事只有真跑一次才知道；在群里试要等半天、还可能被群友看到。 */

    /* 下拉填充小工具：把模型列表填进去，并保留当前值（旧值也留一项，免得保存时被改掉） */
    $("agentServerModelsGo").addEventListener("click", async () => {
      const out = $("agentOut");
      out.textContent = "正在拉模型列表…";
      try {
        await saveSettings();   // 先存接口地址，否则拉的是旧地址
        const r = await api("/api/agent/models?target=server");
        const models = r.models || [];
        if (models.length === 0) {
          out.textContent = `拉不到列表（${r.url || "?"}）：${r.error || "接口没回 data[].id"}`;
          return;
        }

        const pick = $("agentServerModelPick");
        fillSelect(pick, models, "", "（选一个自动填到左边）");
        pick.style.display = "";
        pick.onchange = () => { if (pick.value) $("setAgentServerModel").value = pick.value; };
        out.textContent = `拉到 ${models.length} 个模型（${r.url}）：${models.slice(0, 12).join("、")}${models.length > 12 ? " …" : ""}`;
      } catch (e) {
        out.textContent = "拉取失败：" + e.message;
      }
    });

    /* 刷新外部设备（pi）的模型列表 */
    $("agentHostModelsGo").addEventListener("click", async () => {
      const out = $("agentOut");
      out.textContent = "正在问外部设备有哪些模型…";
      try {
        const r = await api("/api/agent/models?target=host");
        const models = r.models || [];
        fillSelect($("setAgentModel"), models, $("setAgentModel").value, "（用 pi 自己的默认）");
        out.textContent = models.length > 0
          ? `设备上的模型（${models.length} 个）：${models.join("、")}`
          : (r.note || "设备没上报模型列表");
      } catch (e) {
        out.textContent = "拉取失败：" + e.message;
      }
    });

    /* ─────────── 外部设备：一键连接 + 每设备配置 ─────────── */

    // 设备表内存副本（声明在外层作用域：saveSettings 保存时要读它）

    function renderAgentDevices() {
      const box = $("agentDeviceTable");
      if (agentDevices.length === 0) {
        box.innerHTML = '<div style="padding:6px 0">还没有外部设备。点「一键连接本机…」按提示接入。</div>';
        return;
      }

      box.innerHTML = agentDevices.map((d, i) => {
        const online = d.online ? "🟢 在线" : "⚪ 离线";
        const name = escapeHtml(d.name);
        const hint = d.online
          ? ""
          : `<div class="hint" style="margin-top:4px">还没接上来：点「一键连接本机…」把「${name}」这个名字填进去，脚本会在本机报同一个名字；名字不一致会在表里多出一行，把这一行删掉即可。</div>`;

        // 配的模型设备上没有 → 直接标出来（这种错只能靠“填了就能看出不对”来防）
        const models = d.models || [];
        const badModel = d.model && models.length > 0 && !models.includes(d.model);
        const badHint = badModel
          ? `<div class="hint" style="margin-top:4px;color:#c62828">⚠️ 「${escapeHtml(d.model)}」这台设备上没有 —— pi 只认 <code>provider/model</code>（比如 <code>localhost/xxx</code>），不是聊天网关那个模型名；请从下面下拉里重选，或者留空用 pi 默认。</div>`
          : "";
        const modelOptions = models.map((m) =>
          `<option value="${escapeHtml(m)}"${m === d.model ? " selected" : ""}>${escapeHtml(m)}</option>`).join("");
        // 这一行显示的是**生效的**工作目录，并标出它从哪来：
        //   设备专属（面板里给这台设备填的）→ 全局默认（面板里那个「工作目录」）→ 桥自报（仅兵底）
        // 以前只写个目录名，看上去像“设备专属”，其实可能是全局值，改全局时让人以为没生效。
        const devDir = d.workdir || "";
        const gDir = agentGlobalWorkdir || "";
        const effDir = devDir || gDir || d.cwd || "";
        const dirFrom = devDir ? "设备专属" : (gDir ? "全局默认" : "桥自报");
        const dirExtra = devDir && gDir && devDir !== gDir ? ` · 全局默认是 ${escapeHtml(gDir)}` : "";
        const dirHint = effDir ? ` · 目录 ${escapeHtml(effDir)}（${dirFrom}）${dirExtra}` : "";
        // 下面这些值（设备名/模型/目录/工具白名单）全部来自配置与桥上报，一律转义后再拼
        return `<div style="border:1px solid var(--line);border-radius:8px;padding:8px;margin:6px 0">
          <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
            <b>${name}</b>
            <span class="hint">${online}${d.pi ? ` · pi ${escapeHtml(d.pi)}` : ""}${dirHint}</span>
            <label class="switch-row" style="padding:0"><input type="checkbox" data-dev-enable="${i}"${d.enable ? " checked" : ""} /><span class="switch"></span><span class="hint">启用</span></label>
            <button class="ghost-btn" data-dev-toggle="${i}">${d.online ? "断开" : "重连"}</button>
            <button class="ghost-btn" data-dev-del="${i}">删除</button>
          </div>
          ${hint}
          ${badHint}
          <div class="grid-2" style="margin-top:6px">
            <div class="field"><label class="hint">模型</label>
              <select data-dev-model="${i}">
                <option value="">（用 pi 默认）</option>${modelOptions}
                ${d.model && !(d.models || []).includes(d.model) ? `<option value="${escapeHtml(d.model)}" selected>${escapeHtml(d.model)}</option>` : ""}
              </select>
            </div>
            <div class="field"><label class="hint">工作目录</label>
              <input type="text" data-dev-workdir="${i}" value="${escapeHtml(d.workdir || "")}" placeholder="留空 = 用全局默认${agentGlobalWorkdir ? `（${escapeHtml(agentGlobalWorkdir)}）` : "目录"}" />
            </div>
          </div>
          <div class="grid-2">
            <div class="field"><label class="hint">工具白名单（空=全开）</label>
              <input type="text" data-dev-tools="${i}" value="${escapeHtml(d.tools || "")}" placeholder="bash,read,fetch" />
            </div>
            <div class="field"><label class="hint">超时（秒，0=用全局）</label>
              <input type="number" data-dev-timeout="${i}" value="${d.timeoutSec || 0}" min="0" max="7200" step="30" />
            </div>
          </div>
          ${badHint}
        </div>`;
      }).join("");

      box.querySelectorAll("[data-dev-model]").forEach((el) => el.addEventListener("change", () => {
        agentDevices[Number(el.dataset.devModel)].model = el.value;
        agentDevicesEdited = true;
        markSettingsDirty();
      }));
      box.querySelectorAll("[data-dev-workdir]").forEach((el) => el.addEventListener("input", () => {
        agentDevices[Number(el.dataset.devWorkdir)].workdir = el.value.trim();
        agentDevicesEdited = true;
        markSettingsDirty();
      }));
      box.querySelectorAll("[data-dev-tools]").forEach((el) => el.addEventListener("input", () => {
        agentDevices[Number(el.dataset.devTools)].tools = el.value.trim();
        agentDevicesEdited = true;
        markSettingsDirty();
      }));
      box.querySelectorAll("[data-dev-timeout]").forEach((el) => el.addEventListener("input", () => {
        agentDevices[Number(el.dataset.devTimeout)].timeoutSec = Number(el.value) || 0;
        agentDevicesEdited = true;
        markSettingsDirty();
      }));
      box.querySelectorAll("[data-dev-enable]").forEach((el) => el.addEventListener("change", () => {
        agentDevices[Number(el.dataset.devEnable)].enable = el.checked;
        agentDevicesEdited = true;
        markSettingsDirty();
      }));
      box.querySelectorAll("[data-dev-del]").forEach((el) => el.addEventListener("click", () => {
        agentDevices.splice(Number(el.dataset.devDel), 1);
        agentDevicesEdited = true;
        renderAgentDevices();
        markSettingsDirty();
      }));
      box.querySelectorAll("[data-dev-toggle]").forEach((el) => el.addEventListener("click", async () => {
        const d = agentDevices[Number(el.dataset.devToggle)];
        if (d.online) {
          await api("/api/agent/disconnect", { method: "POST", body: JSON.stringify({ device: d.name }) });
          toast(`已断开 ${d.name}（本机那边会自动重连）`);
        } else {
          toast(`离线设备要接上来：点「一键连接本机…」，把名字填成 ${d.name}，在本机跑一次那个脚本`);
        }
        await refreshAgentDevicesFull();
      }));
    }

    async function refreshAgentDevicesFull() {
      const out = $("agentOut");
      try {
        const r = await loadAgentDevices();      // 顶层：草稿 + 已保存快照 + 重画
        fillSelect($("setAgentModel"), r.deviceModels || [], $("setAgentModel").value, "（用 pi 自己的默认）");
        return r;
      } catch (e) {
        out.textContent = "刷新设备失败：" + e.message;
        return null;
      }
    }

    // 注册给 saveSettings()：保存成功后刷新设备表（跨作用域只能这样搭桥，不能直接调用）
    afterSettingsSaved = refreshAgentDevicesFull;
    // 顶层 loadAgentDevices() / resetAgentDeviceDraft() 拿不到 bindUi() 里的渲染函数，注册给它
    renderAgentDeviceList = (r) => {
      renderAgentDevices();
      if (r) refreshAgentDevices(r.devices || []);
    };

    $("agentDeviceAdd").addEventListener("click", () => {
      const name = prompt("设备名（本机桥握手时上报的 host 名，例如 ZHAOSPC；现在也可以随便写，接上来就会匹配）：");
      if (!name) return;
      agentDevices.push({ name: name.trim(), online: false, enable: true, model: "", workdir: "", tools: "", timeoutSec: 0, models: [] });
      agentDevicesEdited = true;
      renderAgentDevices();
      markSettingsDirty();
    });

    $("agentDeviceRefresh").addEventListener("click", async () => {
      await refreshAgentDevicesFull();
      toast("设备状态已刷新");
    });

    /* 一键连接：先问一个设备名（名字要跟本机报上来的一致），再生成带名/带地址/带令牌的脚本 */
    $("agentConnectGo").addEventListener("click", async () => {
      const out = $("agentConnectOut");
      out.style.display = "";

      const suggest = (agentDevices[0] && agentDevices[0].name) || "";
      const name = (prompt(
        "给这台机器起个设备名（面板里按这个名字认设备；本机桥默认报的是主机名，比如 Windows 的 COMPUTERNAME）。\n" +
        "留空 = 用本机主机名。", suggest) || "").trim();

      try {
        await saveSettings();   // 保证服务端用最新的设备配置
      } catch (e) {
        out.textContent = "保存设置失败：" + e.message;
        return;
      }

      // 面板里还没这行设备就先加上（这样接上来就能直接看到并配模型）
      if (name && !agentDevices.some((d) => (d.name || "").toLowerCase() === name.toLowerCase())) {
        agentDevices.push({ name, online: false, enable: true, model: "", workdir: "", tools: "", timeoutSec: 0, models: [] });
        agentDevicesEdited = true;
        renderAgentDevices();
        await saveSettings();
      }

      const isWin = !/Mac|Linux|Android|iPhone|iPad/i.test(navigator.platform || navigator.userAgent || "");
      const os = isWin ? "win" : "sh";
      const url = withToken(`${apiBase()}/api/agent/setup?os=${os}&name=${encodeURIComponent(name)}`);
      out.textContent = [
        name ? `设备名：${name}（脚本里已经带上，本机跑完报上来的就是这个名字）` : `设备名：用本机主机名（脚本里没指定）`,
        "",
        "① 点下面按钮下载启动脚本（里面已经带好地址、令牌、设备名，不用手改）",
        "② 把 pi-bridge.py 也放到本机同一目录（下面给链接）",
        isWin ? "③ 双击运行 connect-pi-bridge.cmd（窗口别关）" : "③ 运行 sh connect-pi-bridge.sh（窗口别关）",
        "④ 回来后点「我已运行，检测连接」，看到 🟢 在线就是成了",
        "",
        "服务器文件：桥会额外把本机一个端口转发到服务器的 sshd（默认 2222），",
        "agent 就能用 sftp/scp 直接读写服务器文件；批量操作可用 server-files.py（上面也能下载）。",
        ""
      ].join("\n");
      out.insertAdjacentHTML("beforeend",
        `<div class="sticker-actions">\n` +
        `  <a class="ghost-btn" href="${url}" download>下载 ${isWin ? "connect-pi-bridge.cmd" : "connect-pi-bridge.sh"}</a>\n` +
        `  <a class="ghost-btn" href="${withToken(`${apiBase()}/agent-bridge-script`)}" download="pi-bridge.py">下载 pi-bridge.py</a>\n` +
        `  <a class="ghost-btn" href="${withToken(`${apiBase()}/agent-sftp-script`)}" download="server-files.py">下载 server-files.py</a>\n` +
        `  <button class="ghost-btn js-agent-connect-check">我已运行，检测连接</button>\n` +
        `</div>`);

      // 注意：这个按钮是动态插进来的，用 class 而不是 id —— 面板探针会去 index.html 里核对
      // “app.js 引用的 id 是否都存在”，动态节点的 id 会被判为不存在（踩过）。
      out.querySelector(".js-agent-connect-check").addEventListener("click", async () => {
        const r = await refreshAgentDevicesFull();
        const list = (r && r.devices) || [];
        out.textContent += list.length > 0
          ? `\n✓ 已连上：${list.join("、")}（模型 ${(r.deviceModels || []).length} 个）`
          : "\n…还没收到连接。确认脚本窗口还开着、令牌没改错。";
      });
    });

    /* ─────────── Agent 会话：总数/标题总览 + 新建/切换/改名/删除/清空 ───────────
       与群里 //sessions / //new / //use / //rename / //del / //reset 是同一套存储。 */

    let agentSessionsCache = [];   // 当前列出的会话（改名时要取旧名字）

    async function refreshAgentSessions() {
      const box = $("agentSessionTable");
      const sel = $("agentSessionChat");
      try {
        const all = await api("/api/agent/sessions");
        const chats = Object.keys(all.chats || {});

        // 聊天下拉：第一项是“全部聊天”总览（号主要“查现在有多少个会话及其标题”）
        const keep = sel.value || "__all__";
        sel.innerHTML = `<option value="__all__">全部聊天（共 ${all.total || 0} 个会话）</option>` +
          chats.map((k, i) => {
            const c = all.chats[k];
            // 聊天也带序号：与群里 //sessions all 的顺序一致（同一个 AllChats 顺序）
            return `<option value="${k}">${i + 1}. ${c.name || k}（${(c.sessions || []).length}）</option>`;
          }).join("");
        sel.value = (keep === "__all__" || chats.includes(keep)) ? keep : "__all__";

        const key = sel.value;
        if (key === "__all__") {
          const lines = chats.map((k) => {
            const c = all.chats[k];
            // 每个会话前面带序号：和群里 //sessions 的顺序一样，面板看第几号、群里 //use 第几号能对上
            const titles = (c.sessions || []).map((x, i) =>
              `${i + 1}) ${x.current ? "← " : ""}${x.name}（${x.backend === "server" ? "服务器" : (x.device || "外部")}·${x.turns}轮）`);
            return `<div style="border:1px solid var(--line);border-radius:8px;padding:6px 8px;margin:6px 0">
              <b>${c.name || k}</b> <span class="hint">${(c.sessions || []).length} 个</span>
              <div class="hint" style="margin-top:4px">${titles.join("　")}</div>
            </div>`;
          });
          box.innerHTML = (all.total ? `<div style="padding:4px 0">会话总数：<b>${all.total}</b> 个，分布在 ${all.chatCount} 个聊天里。</div>` : "") +
            (lines.length ? lines.join("") : '<div style="padding:6px 0">还没有 agent 会话。群里发一条 <code>//指令</code> 就有了。</div>');
          agentSessionsCache = [];
          return;
        }

        const list = (all.chats[key] || {}).sessions || [];
        agentSessionsCache = list;
        box.innerHTML = list.map((x, i) => {
          const where = x.backend === "server" ? "服务器内置" : `外部 ${x.device || "设备"}`;
          const when = x.updatedAt ? new Date(x.updatedAt).toLocaleString() : "";
          const runs = x.runs || [];
          const runsHtml = runs.length === 0
            ? '<div class="hint">还没跑过任务。</div>'
            : runs.map((r) => {
                const st = r.ok === null || r.ok === undefined ? "⏳ 在跑" : (r.ok ? "✅" : "❌");
                const t = r.at ? new Date(r.at).toLocaleString() : "";
                const extra = r.ok === null || r.ok === undefined ? "" : ` · ${(r.durationMs / 1000).toFixed(1)}s${r.toolCalls ? ` · ${r.toolCalls} 次工具` : ""}`;
                return `<div class="hint" style="margin:3px 0">${st} ${t}${extra}｜${r.prompt || ""}${r.result ? ` → ${r.result}` : ""}</div>`;
              }).join("");
          return `<div style="border:1px solid var(--line);border-radius:8px;padding:6px 8px;margin:6px 0">
            <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
              <b>#${i + 1} ${x.current ? "← " : ""}${x.name}</b>
              <span class="hint">[${where}] ${x.turns} 轮 · ${when}${x.historyChars ? ` · 上下文 ${x.historyChars} 字` : ""}${x.autoNamed ? " · 自动标题" : ""}${x.piOwned === false ? " · pi 导入" : ""} · 跑过 ${runs.length} 次</span>
              <button class="ghost-btn" data-sess-use="${x.id}"${x.current ? " disabled" : ""}>切到这个</button>
              <button class="ghost-btn" data-sess-rename="${x.id}">改名</button>
              <button class="ghost-btn" data-sess-reset="${x.id}">清空</button>
              <button class="ghost-btn" data-sess-del="${x.id}">删除</button>
              <button class="ghost-btn" data-sess-runs="${x.id}">执行记录</button>
            </div>
            <div class="hint" style="margin-top:4px">${x.piSession ? `pi 会话：${x.piSession}` : ""}</div>
            <div data-sess-runs-box="${x.id}" style="display:none;margin-top:6px;border-top:1px dashed var(--line);padding-top:6px">${runsHtml}</div>
          </div>`;
        }).join("");

        box.querySelectorAll("[data-sess-use]").forEach((el) => el.addEventListener("click", async () => {
          await api("/api/agent/sessions", { method: "POST", body: JSON.stringify({ key: sel.value, action: "use", id: el.dataset.sessUse }) });
          await refreshAgentSessions();
        }));
        box.querySelectorAll("[data-sess-runs]").forEach((el) => el.addEventListener("click", () => {
          const box2 = box.querySelector(`[data-sess-runs-box="${el.dataset.sessRuns}"]`);
          if (box2) box2.style.display = box2.style.display === "none" ? "" : "none";
        }));
        box.querySelectorAll("[data-sess-rename]").forEach((el) => el.addEventListener("click", async () => {
          const old = agentSessionsCache.find((x) => x.id === el.dataset.sessRename) || {};
          // 改名输入框要填**真名**（nameRaw）：显示用的 name 在脱敏开启时是“群友A”这种占位符，
          // 拿它去改名会把占位符写回去
          const title = prompt("新的会话标题：", old.nameRaw || old.name || "");
          if (!title) return;
          await api("/api/agent/sessions", { method: "POST", body: JSON.stringify({ key: sel.value, action: "rename", id: el.dataset.sessRename, title }) });
          await refreshAgentSessions();
          toast("已改名");
        }));
        box.querySelectorAll("[data-sess-reset]").forEach((el) => el.addEventListener("click", async () => {
          if (!confirm("清空这个会话的历史？（群里下一句 // 就从零开始）")) return;
          await api("/api/agent/sessions", { method: "POST", body: JSON.stringify({ key: sel.value, action: "reset", id: el.dataset.sessReset }) });
          await refreshAgentSessions();
          toast("已清空");
        }));
        box.querySelectorAll("[data-sess-del]").forEach((el) => el.addEventListener("click", async () => {
          if (!confirm("删除这个会话？（外部设备上的历史文件也会一起删）")) return;
          await api("/api/agent/sessions", { method: "POST", body: JSON.stringify({ key: sel.value, action: "delete", id: el.dataset.sessDel }) });
          await refreshAgentSessions();
          toast("已删除");
        }));
      } catch (e) {
        box.textContent = "读会话失败：" + e.message;
      }
    }

    $("agentSessionRefresh").addEventListener("click", async () => {
      await refreshAgentSessions();
      toast("会话已刷新");
    });

    $("agentSessionChat").addEventListener("change", refreshAgentSessions);

    /* 从 pi 导入：把设备上已有的 pi 会话接过来当会话（号主：外部 Agent 则获取 pi 里面的会话） */
    $("agentSessionImport").addEventListener("click", async () => {
      const key = $("agentSessionChat").value;
      if (!key || key === "__all__") { toast("先在左边选一个具体的聊天，再导入"); return; }
      const out = $("agentSessionTable");
      out.innerHTML = '<div class="hint">正在问设备上有哪些 pi 会话…</div>';
      try {
        const r = await api("/api/agent/pi-sessions");
        const list = r.sessions || [];
        if (!r.connected) { out.innerHTML = '<div class="hint">外部设备不在线，列不出 pi 会话。</div>'; return; }
        if (list.length === 0) { out.innerHTML = '<div class="hint">设备上没找到 pi 会话（~/.pi/agent/sessions/… 为空？）。</div>'; return; }

        out.innerHTML = `<div class="hint" style="padding:4px 0">设备上的 pi 会话（${list.length} 个，最新的在前）——点「接用」把它变成这个聊天的一个会话：</div>` +
          list.slice(0, 20).map((it) => {
            const when = it.mtime ? new Date(it.mtime * 1000).toLocaleString() : "";
            return `<div style="border:1px solid var(--line);border-radius:8px;padding:6px 8px;margin:6px 0;display:flex;gap:8px;align-items:center;flex-wrap:wrap">
              <b>${(it.title || "(无标题)").slice(0, 40)}</b>
              <span class="hint">${when} · ${it.cwd || ""} · ${it.id.slice(0, 12)}…</span>
              <button class="ghost-btn" data-pi-import="${it.id}">接用</button>
            </div>`;
          }).join("");

        out.querySelectorAll("[data-pi-import]").forEach((el) => el.addEventListener("click", async () => {
          const resp = await api("/api/agent/sessions", {
            method: "POST",
            body: JSON.stringify({ key, action: "import", piSession: el.dataset.piImport })
          });
          toast(resp.message || "已接用");
          await refreshAgentSessions();
        }));
      } catch (e) {
        out.innerHTML = '<div class="hint">拉取失败：' + e.message + '</div>';
      }
    });

    // ─────────── 本机 Agent（// 命令）───────────
    // 「恢复默认」：默认那份在服务端（AppSettings.DefaultAgentPrompt），前端不抄一份，免得两处漂移
    $("agentPromptReset").addEventListener("click", () => {
      if (!state.agentPromptDefault) { toast("还没从服务端拿到默认提示词"); return; }
      $("setAgentPrompt").value = state.agentPromptDefault;
      toast("已填入默认提示词 —— 别忘了点保存");
    });

    $("agentSessionNew").addEventListener("click", async () => {
      const key = $("agentSessionChat").value;
      if (!key || key === "__all__") { toast("先在左边选一个具体的聊天（群/好友），再新建会话"); return; }
      const name = prompt("新会话名字（可空 —— 空的话，跑完一轮会按内容自动总结标题）：", "") || "";
      const backend = ($("setAgentTargetMode").value === "server" || !$("setEnableHostAgent").checked) ? "server" : "host";
      try {
        const r = await api("/api/agent/sessions", { method: "POST", body: JSON.stringify({ key, action: "new", name, backend }) });
        await refreshAgentSessions();
        toast(r.message || "已新建");
      } catch (e) {
        toast("新建失败：" + e.message);
      }
    });

    $("agentStatusGo").addEventListener("click", async () => {
      const out = $("agentOut");
      out.textContent = "查询中…";
      try {
        const r = await api("/api/agent/status");
        refreshAgentDevices(r.devices || []);
        fillSelect($("setAgentModel"), r.deviceModels || [], r.agentModel || "", "（用 pi 自己的默认）");
        refreshAgentSessions();
        out.textContent = [
          `开关：总体${r.enabled ? "开" : "关"}｜外部设备 ${r.hostAgent === false ? "关" : "开"}｜服务器 ${r.serverAgent ? "开" : "关"}`,
          `前缀：${r.prefix}`,
          `可用 QQ：${r.allowedUsers || "（空 —— 谁都不能用）"}`,
          `令牌：${r.tokenConfigured ? "已配置" : "没有配置（外部设备连不上）"}`,
          `优先：${r.target}（auto = 外部在线优先）`,
          `在线设备：${r.connected ? (r.devices || []).join("、") : "不在线"}`,
          `当前：${r.summary}`
        ].join("\n");
      } catch (e) {
        out.textContent = "查询失败：" + e.message;
      }
    });

    /* 设备下拉：在线设备自动出现；旧值（比如已下线的设备）保留一项，免得保存时被改掉 */
    function refreshAgentDevices(devices) {
      const sel = $("setAgentDevice");
      const keep = sel.value;
      const list = Array.from(new Set([...(devices || []), keep].filter(Boolean)));
      sel.innerHTML = '<option value="">（不指定，跟着上面）</option>' +
        list.map((d) => `<option value="${escapeHtml(d)}">${escapeHtml(d)}</option>`).join("");
      sel.value = list.includes(keep) ? keep : "";
    }

    $("agentTestGo").addEventListener("click", async () => {
      const out = $("agentOut");
      const prompt = $("agentTestPrompt").value.trim();
      if (!prompt) { toast("先写一句要它干的活"); return; }
      const target = $("agentTestTarget").value;
      out.textContent = `已送到${target === "server" ? "服务器 agent" : "外部设备"}，正在跑…（长任务可能要几分钟，别关页面）`;
      try {
        await saveSettings();   // 先用当前设置，不然测的是旧配置
        const r = await api("/api/agent/test", {
          method: "POST",
          body: JSON.stringify({ prompt, timeoutSec: 300, target })
        });
        const secs = r.durationMs ? (r.durationMs / 1000).toFixed(1) : "?";
        out.textContent = `${r.ok ? "✅ 成功" : "❌ 失败"}（${r.target || target}，${secs}s${r.toolCalls ? `，${r.toolCalls} 次工具` : ""}）：\n${r.text || "（没有输出）"}`;
      } catch (e) {
        out.textContent = "失败：" + e.message;
      }
    });

    // ─────────── 服务器健康日报（定时私聊推送）───────────
    // 函数体在顶层（紧跟着 bindLogScrollButtons 后面）—— 放这里的话 loadSettings() 调不到它：
    // 那会抛 ReferenceError，而 loadSettings 的 .catch 会把它吞成“保存按钮点了没反应”（踩过，handoff §31.9）
    $("healthReportPreviewGo").addEventListener("click", () => runHealthReport("preview"));
    $("healthReportSendGo").addEventListener("click", () => runHealthReport("send"));

    $("searchTestGo").addEventListener("click", async () => {
      const out = $("searchTestOut");
      const value = $("searchTestQuery").value.trim();
      if (!value) { toast("先填个搜索词或网址"); return; }
      const isUrl = /^https?:\/\//i.test(value);
      out.textContent = isUrl ? "正在读页面…" : "正在搜索…（模型自带搜索要等几秒）";
      try {
        await saveSettings();   // 先用当前设置，不然测的是旧配置
        const r = await api("/api/search/test", {
          method: "POST",
          body: JSON.stringify(isUrl ? { url: value } : { query: value })
        });
        out.textContent = (r && (r.note || r.text)) || ("没查到：" + ((r && r.error) || "上游无返回"));
      } catch (err) {
        out.textContent = "失败：" + ((err.data && err.data.error) || err.message);
      }
    });

    $("voiceHealthGo").addEventListener("click", async () => {
      const out = $("voiceTestHint");
      out.textContent = "正在问 TTS 服务…";
      try {
        const r = await api("/api/voice/health");
        const voices = (r && r.voices) || [];
        out.textContent = `TTS 正常：${r.url}；当前音色 ${r.currentVoice}；可用 ${voices.join("、") || "(没列出)"}`;
        fillVoiceOptions(voices);
      } catch (err) {
        const detail = (err.data && err.data.error) || err.message;
        const url = err.data && err.data.url ? `（地址 ${err.data.url}）` : "";
        out.textContent = "TTS 不可用：" + detail + url;
      }
    });

    /* 音色候选项以“服务端实际装了的”为准。
       以前写死四个（模拟器里的 onnx 就没装），选到没装的就只能报错。 */
    function fillVoiceOptions(voices) {
      if (!voices || !voices.length) return;
      const list = $("voiceNameOptions");
      if (!list) return;
      const current = $("setVoiceName").value.trim();
      const all = voices.includes(current) || !current ? voices : [current, ...voices];
      list.replaceChildren(...all.map((v) => Object.assign(document.createElement("option"), { value: v })));
    }

    // 点开音色输入框时顺手拉一次“TTS 服务装了哪些音色”（失败就保持原样，不预置静态候选）。
    // 不放在 loadSettings 里：那里是“保存契约”的关键路径，不该加网络请求。
    $("setVoiceName").addEventListener("focus", async () => {
      try {
        const r = await api("/api/voice/health");
        fillVoiceOptions((r && r.voices) || []);
      } catch (err) {
        // 忽略：TTS 没起来时不该阻塞设置页
      }
    });

    // 网易云扫码登录：拿二维码 → 每 2.5 秒问一次状态 → 803 = 成功
    let qrKey = null;
    let qrTimer = null;
    const qrState = (t) => { $("neteaseLoginState").textContent = t; };

    function stopQrPolling() {
      if (qrTimer) { clearInterval(qrTimer); qrTimer = null; }
    }

    async function startNeteaseLogin() {
      stopQrPolling();
      $("neteaseQr").hidden = true;
      qrState("正在获取二维码…");
      try {
        const r = await api("/api/netease/qr", { method: "POST", body: "{}" });
        if (!r || !r.qrimg) {
          qrState("拿不到二维码：" + ((r && r.error) || "（自建接口不可用）"));
          return;
        }

        qrKey = r.key;
        $("neteaseQr").src = r.qrimg;
        $("neteaseQr").hidden = false;
        qrState("请用网易云 App 扫码");

        qrTimer = setInterval(async () => {
          try {
            const s = await api("/api/netease/qr/check", { method: "POST", body: JSON.stringify({ key: qrKey }) });
            const code = s && s.code;
            if (code === 800) { qrState("二维码已过期，重新点“扫码登录”"); stopQrPolling(); $("neteaseQr").hidden = true; }
            else if (code === 801) { qrState("等待扫码…"); }
            else if (code === 802) { qrState("已扫码，请在手机上确认"); }
            else if (code === 803) {
              // 登录态已存在库里（重启/重建容器都不会掉）——把这件事说清楚，不然下次看到“未登录”又会以为要重扫
              qrState(s && s.saved === false
                ? "✅ 已登录（但登录态落盘失败，重启后可能要重扫）"
                : "✅ 已登录，登录态已保存（重启 / 重建容器都不丢）");
              $("neteaseQr").hidden = true;
              stopQrPolling();
              toast("网易云登录成功（登录态已保存）");
            }
            else if (s && s.error) { qrState("查状态失败：" + s.error); }
          } catch (err) {
            qrState("查状态失败：" + err.message);
          }
        }, 2500);
      } catch (err) {
        qrState("登录请求失败：" + err.message);
      }
    }

    $("neteaseLogin").addEventListener("click", startNeteaseLogin);
    wireDeployCard();
    wireCloneCard();
    loadClonedVoices();
  }

  /* ─────────── 面板一键部署（上传/拉取产物 → 重建镜像 → 替换自己）───────────
     部署会把我们这个容器换掉 —— 页面会断一下，所以起完先轮询 /healthz，
     回来后再拉一次状态与日志（否则界面会永远停在“正在重建…”）。 */
  async function loadDeployStatus() {
    const box = $("deployStatus");
    if (!box) return null;
    try {
      const d = await api("/api/deploy");
      const lines = [
        `开关：${d.enabled ? "已开启" : "关着（上传/拉取都会被拒）"}`,
        d.tarBytes ? `当前产物：${fmtSize(d.tarBytes)}　更新于 ${String(d.tarUpdated || "").replace("T", " ").slice(0, 19)}` : "当前产物：（还没有）",
        `当前镜像：${d.image || "（读不到：" + (d.docker || "") + "）"}`,
        `上一个镜像（回滚点）：${d.previousImage || "（还没有）"}`,
        `容器启动于：${d.containerStarted || "?"}`,
        d.lastExit ? `上次部署退出码：${d.lastExit}` : ""
      ].filter(Boolean);
      box.textContent = lines.join("\n");
      $("deployLog").style.display = d.logTail ? "" : "none";
      $("deployLog").textContent = d.logTail || "";
      return d;
    } catch (e) {
      box.textContent = "读不到部署状态：" + e.message;
      return null;
    }
  }

  async function waitPanelBack(maxSeconds) {
    // 容器被替换时页面会断开：每 3 秒探一次 /healthz，回来就算成
    const deadline = Date.now() + (maxSeconds || 180) * 1000;
    const log = $("deployLog");
    log.style.display = "";
    let ticks = 0;
    while (Date.now() < deadline) {
      ticks++;
      log.textContent = `正在重建镜像并替换容器…（已等 ${ticks * 3} 秒，页面会自动回来）`;
      await new Promise((r) => setTimeout(r, 3000));
      try {
        const res = await fetch(withToken(`${apiBase()}/healthz`), { cache: "no-store" });
        if (res.ok) {
          await new Promise((r) => setTimeout(r, 2000));
          await loadDeployStatus();
          toast("面板已回来，部署日志如下");
          return true;
        }
      } catch (e) { /* 容器还没起来，继续等 */ }
    }
    log.textContent = "等了 3 分钟还没回来：去服务器看一眼 docker ps -a 与 /opt/qqchat/deploy.last.log";
    return false;
  }

  async function deployUpload() {
    const input = $("deployUpload");
    const file = input.files && input.files[0];
    if (!file) { toast("先选一个 app.tar.gz"); return; }
    if (!confirm(`上传 ${file.name}（${fmtSize(file.size)}）并用它重建机器人？\n\n部署期间机器人会短暂重启（NapCat 不会被重建）。`)) return;
    if (!confirm("再确认一次：部署会替换正在运行的机器人容器，继续？")) return;
    try {
      const res = await fetch(withToken(`${apiBase()}/api/deploy/upload?filename=${encodeURIComponent(file.name)}`), {
        method: "POST", headers: authHeaders({ "Content-Type": "application/gzip" }), body: file
      });
      const text = await res.text();
      if (!res.ok) throw new Error(text.slice(0, 300));
      input.value = "";
      await waitPanelBack(240);
    } catch (e) { toast("部署失败：" + e.message); }
  }

  async function deployByUrl() {
    const url = ($("deployUrl").value || "").trim();
    if (!url) { toast("先填产物地址（http/https）"); return; }
    if (!confirm(`让服务器去拉 ${url} 并用它重建机器人？\n\n部署期间机器人会短暂重启。`)) return;
    try {
      const r = await api("/api/deploy/url", { method: "POST", body: JSON.stringify({ url }) });
      if (!r.started) throw new Error(JSON.stringify(r));
      await waitPanelBack(240);
    } catch (e) { toast("部署失败：" + e.message); }
  }

  async function deployRollback() {
    if (!confirm("回滚到上一个镜像（qqchat-agent:prev）并重启机器人？")) return;
    try {
      const r = await api("/api/deploy/rollback", { method: "POST", body: "{}" });
      if (!r.started) throw new Error(JSON.stringify(r));
      await waitPanelBack(240);
    } catch (e) { toast("回滚失败：" + e.message); }
  }

  /* ─────────── 音色复刻（面板读文件 → base64 → 服务端上传云端）─────────── */
  async function loadClonedVoices() {
    const box = $("cloneList");
    if (!box) return;
    box.textContent = "";
    try {
      const d = await api("/api/voice/clones");
      if (d.error) {
        box.textContent = "复刻音色：" + d.error;
        return;
      }

      const list = d.voices || [];
      if (!list.length) {
        box.textContent = "还没有复刻过音色。";
        return;
      }

      // 一行一个：[用它] [删除]，而不是一大串纯文本（要能删才叫管理）
      const head = document.createElement("div");
      head.textContent = "已复刻的 " + list.length + " 个音色：";
      box.appendChild(head);
      for (const id of list) {
        const row = document.createElement("div");
        row.style.cssText = "display:flex;align-items:center;gap:8px;margin-top:4px";

        const name = document.createElement("code");
        name.textContent = id;
        row.appendChild(name);

        const use = document.createElement("button");
        use.type = "button";
        use.className = "btn-secondary";
        use.textContent = "用它";
        use.title = "填进上面的「音色」格（还要点保存才生效）";
        use.addEventListener("click", () => { $("setVoiceName").value = id; $("cloneHint").textContent = "已填入音色：" + id + " —— 记得点保存。"; });
        row.appendChild(use);

        const del = document.createElement("button");
        del.type = "button";
        del.className = "btn-secondary";
        del.textContent = "删除";
        del.title = "从云端删掉这个克隆音色（只能删克隆的，系统音色删不掉）";
        del.addEventListener("click", async () => {
          if (!confirm("删掉音色 " + id + "？\n\n云端会真的删除它；正在用它的会话之后会发不出语音（换成别的音色即可）。")) return;
          del.disabled = true;
          del.textContent = "删除中…";
          try {
            const r = await api("/api/voice/clone/delete", { method: "POST", body: JSON.stringify({ voiceId: id }) });
            if (r.ok) {
              $("cloneHint").textContent = "已删除音色：" + id;
              loadClonedVoices();
            } else {
              $("cloneHint").textContent = "删除失败：" + (r.error || "未知原因");
              del.disabled = false;
              del.textContent = "删除";
            }
          } catch (e) {
            $("cloneHint").textContent = "删除失败：" + e.message;
            del.disabled = false;
            del.textContent = "删除";
          }
        });
        row.appendChild(del);
        box.appendChild(row);
      }
    } catch (e) {
      box.textContent = "读不到复刻列表：" + e.message;
    }
  }

  /* 读一段音频的时长（秒）；读不到返回 0（浏览器解不了就只好靠大小猜）。 */
  function audioDuration(file) {
    return new Promise((resolve) => {
      const url = URL.createObjectURL(file);
      const el = document.createElement("audio");
      let done = false;
      const finish = (v) => { if (!done) { done = true; URL.revokeObjectURL(url); resolve(v || 0); } };
      el.preload = "metadata";
      el.onloadedmetadata = () => finish(el.duration && isFinite(el.duration) ? el.duration : 0);
      el.onerror = () => finish(0);
      setTimeout(() => finish(0), 4000); // 个别格式解不出来，别卡住整个流程
      el.src = url;
    });
  }

  /* 多选时的处理：**全都发给服务端去拼**（面板只负责把时长算出来提示用户）。
     为什么要服务端拼：官方主样本要求 ≥ 10 秒，而号主手上常常是几段 5 秒切片 ——
     让人先去装 ffmpeg 不如服务端接（wav 无损拼 / mp3 按帧拼，见 VoiceService.ConcatSamples）。 */
  async function pickCloneSample(files) {
    const list = Array.from(files);
    let total = 0;
    for (const f of list) total += await audioDuration(f);
    return { files: list, totalSeconds: total };
  }

  // 官方通道见过的会话 → 点一下填进官方白名单（别名号，绝不是真实群号）
  function renderOfficialConversations(list) {
    const box = $("officialConversationList");
    if (!box) return;
    box.textContent = "";
    if (!list || !list.length) {
      box.textContent = "官方通道还没收到过消息（收到后这里会列出别名号，点一下就能填进白名单）。";
      return;
    }

    const head = document.createElement("div");
    head.textContent = "官方通道见过的会话（别名号）：";
    box.appendChild(head);
    for (const item of list) {
      const row = document.createElement("div");
      row.style.cssText = "display:flex;align-items:center;gap:8px;margin-top:4px";
      const label = document.createElement("code");
      label.textContent = (item.isGroup ? "群 " : "单聊 ") + item.id;
      row.appendChild(label);
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "btn-secondary";
      btn.textContent = "加入白名单";
      btn.addEventListener("click", () => {
        const target = item.isGroup ? $("setOfficialWhitelistGroups") : $("setOfficialWhitelistPrivates");
        const cur = target.value.split(/[\s,;，；]+/).filter(Boolean);
        if (!cur.includes(item.id)) cur.push(item.id);
        target.value = cur.join("\n");
        $("officialHint").textContent = "已加入白名单：" + item.id + " —— 记得点保存（保存后立即生效，不用重启）。";
      });
      row.appendChild(btn);
      box.appendChild(row);
    }
  }

  function wireCloneCard() {
    const btn = $("cloneGo");
    if (!btn) return;
    btn.addEventListener("click", async () => {
      const hint = $("cloneHint");
      const files = $("cloneSample").files;
      const voiceId = $("cloneVoiceId").value.trim();
      if (!files || files.length === 0) { hint.textContent = "先选一段音频（10 秒 ~ 5 分钟，mp3/m4a/wav）。"; return; }
      if (!/^[a-z][a-z0-9_-]{7,63}$/.test(voiceId)) {
        hint.textContent = "音色 ID 要 8~64 位、全小写、以字母开头（例如 zhao_voice_01）—— 不合规的 ID 云端只回一句笼统的 2013，很难查。";
        return;
      }

      btn.disabled = true;
      try {
        hint.textContent = "正在读样本…";
        const picked = await pickCloneSample(files);
        const list = picked.files;
        const minutes = picked.totalSeconds / 60;
        if (picked.totalSeconds > 0 && (picked.totalSeconds < 10 || picked.totalSeconds > 300)) {
          hint.textContent = `这些样本合起来约 ${Math.round(picked.totalSeconds)} 秒，官方要求 10 秒 ~ 5 分钟`
            + (list.length > 1 ? "（拼接后的总长）" : "") + " —— 请增删几段再试。";
          return;
        }

        const heavy = list.find((f) => f.size > 20 * 1024 * 1024);
        if (heavy) {
          hint.textContent = `${heavy.name} 有 ${fmtSize(heavy.size)}，超过官方 20MB 上限 —— 先剪短一点。`;
          return;
        }

        hint.textContent = list.length > 1
          ? `正在读取 ${list.length} 段样本（合计约 ${Math.round(picked.totalSeconds)} 秒）…服务端会拼成一段再传`
          : "正在读取样本…";

        async function toBase64(file) {
          const buf = await file.arrayBuffer();
          let bin = "";
          const bytes = new Uint8Array(buf);
          for (let i = 0; i < bytes.length; i += 0x8000) {
            bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
          }
          return btoa(bin);
        }

        let payload;
        if (list.length > 1) {
          const samples = [];
          for (const f of list) samples.push({ audioBase64: await toBase64(f), fileName: f.name });
          payload = { samples: samples, voiceId: voiceId };
          hint.textContent = `正在上传并拼接 ${list.length} 段，然后复刻（一般 10~60 秒）…`;
        } else {
          payload = { audioBase64: await toBase64(list[0]), fileName: list[0].name, voiceId: voiceId };
          hint.textContent = "正在上传并复刻（一般 10~60 秒，取决于样本大小）…样本 " + list[0].name
            + (picked.totalSeconds ? "（约 " + Math.round(picked.totalSeconds) + " 秒）" : "") + "，音色 ID " + voiceId;
        }

        const d = await api("/api/voice/clone", { method: "POST", body: JSON.stringify(payload) });
        if (d.ok) {
          $("setVoiceName").value = d.voiceId;
          hint.textContent = "复刻成功：" + d.voiceId
            + (list.length > 1 ? "（" + list.length + " 段拼接，约 " + Math.round(picked.totalSeconds) + " 秒）" : "（样本 " + list[0].name + "）")
            + (d.note ? " " + d.note : "")
            + " —— 已填进上面的「音色」格，记得点保存。";
          loadClonedVoices();
        } else {
          hint.textContent = "复刻失败：" + (d.error || "未知原因")
            + (picked.totalSeconds && picked.totalSeconds < 10 ? "\n→ 合计只有约 " + Math.round(picked.totalSeconds) + " 秒，官方要求 ≥ 10 秒。" : "");
        }
      } catch (e) {
        hint.textContent = "复刻失败：" + e.message;
      } finally {
        btn.disabled = false;
      }
    });

    // 列出来的复刻音色点一下就填进「音色」格（现在那行也带了按钮，保留这段做兼容）
    $("cloneList").addEventListener("click", (e) => {
      if (e.target && e.target.tagName === "BUTTON") return; // 别抢按钮的活
      const t = (e.target && e.target.textContent || "").trim();
      if (/^[a-z][a-z0-9_-]{7,63}$/.test(t)) $("setVoiceName").value = t;
    });
  }

  function wireDeployCard() {
    const go = $("deployUploadGo");
    if (!go) return;
    go.addEventListener("click", deployUpload);
    $("deployUrlGo").addEventListener("click", deployByUrl);
    $("deployRollbackGo").addEventListener("click", deployRollback);
    loadDeployStatus();
  }

  async function boot() {
    bindUi();
    bindAudioSources();
    initSettingsNav();
    foldCardNotes();
    renderAiMode();
    renderMessages();
    syncMobileView();   // 刷新后回到列表视图，不要停在某个会话上

    try {
      const data = await api("/api/state");
      applyState(data);
      // 默认打开第一个会话；但手机端不这么做 —— 窄屏是主从式导航，
      // 一上来就进会话会让人失去方向（返回键旁边只剩一个会话名）。
      const narrow = typeof matchMedia === "function" && matchMedia("(max-width: 760px)").matches;
      if (!state.activeKey && !narrow && state.conversations.length > 0) {
        await selectConversation(state.conversations[0].key);
      }
    } catch (e) {
      toast("加载状态失败：" + e.message);
    }

    connectEvents();
    // 先去拉一次日志历史（刷新页面后能立刻看到之前的行，而不是空白等到下一条）
    loadLogs();

    // 面板一登录就把扫码卡片对齐一次（账号没在线时立刻去要一张二维码）
    renderLoginCard();
    startLoginWatch();
    if (needsLogin()) pollLogin(false);
  }

  document.addEventListener("DOMContentLoaded", boot);
})();
