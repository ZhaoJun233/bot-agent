/**
 * 面板前端冒烟测试（无需浏览器）
 *
 * 为什么需要它：
 *   面板的“保存设置”有一条隐性契约 —— **loadSettings() 回填的字段集合，
 *   必须覆盖 saveSettings() 发送的字段集合**。
 *   一旦某个字段只在保存时被读取、却没在加载时被回填，它的值就是空的：
 *   `Number("")` → 0 → 服务端把该设置压到最小值；若是白名单，则直接清空
 *   （严格模式 = 忽略全部消息）并删掉所有会话。这类问题在浏览器里表现为
 *   “设置保存不了”，而且没有任何报错，极难排查。
 *
 * 本脚本用最小 DOM 桩把 app.js 真跑一遍，静态 + 动态两道检查：
 *   1. app.js 引用的每个 DOM id 都必须在 index.html 中存在
 *   2. 动态：boot() 不抛异常；loadSettings() 后每个待保存字段都有合法值；
 *      保存时确实发出 POST 且 payload 与表单一致；表单未加载时保存被拒绝
 *
 * 运行：node tests/QQChatAgent.FrontendProbe/probe.mjs
 * 退出码：0 = 全部通过，1 = 有失败项
 */
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../../src/QQChatAgent.Headless/wwwroot");
const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
const js = fs.readFileSync(path.join(root, "app.js"), "utf8");

let pass = 0;
const failures = [];

function check(desc, ok, detail) {
  if (ok) {
    pass++;
    console.log("  ✓ " + desc);
  } else {
    failures.push(desc + (detail ? `　→ ${detail}` : ""));
    console.log("  ✗ " + desc + (detail ? `\n      → ${detail}` : ""));
  }
}

/* ─────────── 1) id 静态检查 ─────────── */

console.log("▶ 静态：DOM id 引用完整性");
const htmlIds = new Set([...html.matchAll(/id="([A-Za-z0-9_-]+)"/g)].map((m) => m[1]));
const jsIds = new Set([...js.matchAll(/\$\("([A-Za-z0-9_-]+)"\)/g)].map((m) => m[1]));
const missingIds = [...jsIds].filter((id) => !htmlIds.has(id));
check(`app.js 引用的 ${jsIds.size} 个 id 全部存在`, missingIds.length === 0, missingIds.join(", "));

/* ─────────── 1b) 保存按钮必须常驻可见 ─────────── */

console.log("\n▶ 静态：保存按钮必须常驻可见（不能藏在滚动区底部）");

const css = fs.readFileSync(path.join(root, "app.css"), "utf8");

/** 取出某个 class 元素及其完整内部 HTML（用 <div> 深度计数找闭合） */
function elementHtml(source, cls) {
  const at = source.indexOf(`class="${cls}"`);
  if (at < 0) return null;
  const start = source.lastIndexOf("<", at);
  let depth = 0;
  for (let i = start; i < source.length; ) {
    if (source.startsWith("<div", i)) { depth++; i += 4; }
    else if (source.startsWith("</div>", i)) { depth--; i += 6; if (depth === 0) return source.slice(start, i); }
    else i++;
  }
  return null;
}

const innerHtml = elementHtml(html, "settings-inner");
const footerHtml = elementHtml(html, "settings-footer");

check("设置页有常驻操作栏 .settings-footer", footerHtml !== null);
check("设置页用纵向布局（page-column）", /class="page page-column"\s+id="pageSettings"/.test(html));
check("★ 保存按钮在常驻操作栏里（不随内容滚动）", !!footerHtml && footerHtml.includes('id="saveBtn"'));
check(
  "★ 保存按钮不在可滚动的内容区里",
  !!innerHtml && !innerHtml.includes('id="saveBtn"'),
  "它被放回了 settings-inner —— 页面一长就又会「没有保存按钮」"
);
check("成功提示条也在常驻栏里", !!footerHtml && footerHtml.includes('id="saveBar"'));
check("CSS：.page-column 纵向", /\.page-column\s*\{[^}]*flex-direction:\s*column/.test(css));
check("CSS：.settings-footer 不参与缩放", /\.settings-footer\s*\{[^}]*flex:\s*0 0 auto/.test(css));
check(
  "CSS：.settings-scroll 可滚动且不被撑开",
  /\.settings-scroll\s*\{[^}]*overflow-y:\s*auto/.test(css) && /\.settings-scroll\s*\{[^}]*min-height:\s*0/.test(css)
);

/* ─────────── 1b2) 分节导航（卡片一多就得滑半天） ─────────── */

console.log("\n▶ 静态：设置页分节导航");

const navMatch = html.match(/<nav class="section-nav" id="settingsNav"[^>]*>\s*<\/nav>/);
check("设置页有分节导航容器 #settingsNav", navMatch !== null);
check(
  "★ 导航在滚动区外面（内容是斜着滚的，它不能跟着跑）",
  html.indexOf('id="settingsNav"') > 0 &&
    html.indexOf('id="settingsNav"') < html.indexOf('class="settings-scroll"'),
  "它得是 #pageSettings 的直子元素，排在 .settings-scroll 前面"
);
check(
  "CSS：导航常驻不缩放 + 窄屏可横向滑（手机上 10 个胶囊放不下）",
  /\.section-nav\s*\{[^}]*flex:\s*0 0 auto/.test(css) && /\.section-nav\s*\{[^}]*overflow-x:\s*auto/.test(css)
);
check("CSS：胶囊有 hover 与 active 态（高亮当前所在的那一节）",
  /\.section-link:hover/.test(css) && /\.section-link\.active/.test(css));
check(
  "★ JS：胶囊从卡片标题生成（不维护第二份硬编码列表）",
  js.includes('initSettingsNav') && js.includes('"settingsNav"') && /querySelector\("h3"\)/.test(js),
  "加一张卡片就得同步改一遍导航列表的话，早晚会对不上"
);
check("JS：boot() 里真的调用了", /initSettingsNav\(\);/.test(js));
check(
  "★ 宽屏改成左侧分节栏 + 单列内容（不再把卡片铺成“一块一块”）",
  /#pageSettings\s*\{[^}]*grid-template-columns:\s*208px/.test(css) &&
    /\.settings-inner\s*\{[^}]*max-width:\s*980px/.test(css) &&
    /\.section-nav\s*\{[^}]*flex-direction:\s*column/.test(css) &&
    !/[\s{;]columns:\s*\d+px/.test(css),
  "高矮不一的卡片横着铺开就是“一块一块”的乱（号主原话），改成一节一节看"
);
check(
  "★ 隐藏的那一节真的不占位（.card 的 display:flex 会盖掉 [hidden] 默认样式）",
  /\.card\[hidden\]\s*\{[^}]*display:\s*none/.test(css)
);
check(
  "★ JS：切换分节只动 hidden，不碰表单字段（保存契约不变）",
  js.includes("showSection") && /c\.hidden = !all && k !== current/.test(js)
);
check(
  "★ 留了“全部显示”出口（单列堆到尾，方便通读 / Ctrl+F 找字段）",
  js.includes("section-link-all") && js.includes('"#sec-all"')
);
check("刷新/带 hash 进来能回到同一节", /#sec-\(\\d\+\|all\)/.test(js) && js.includes("replaceState"));
check(
  "隐藏卡片里的说明不打“已处理”标记（否则切过去就再也不折了）",
  /card\.hidden\)\s*continue/.test(js)
);

/* ─────────── 1b3) 可读性：不把字堆成一团 ─────────── */

console.log("\n▶ 静态：设置页排版可读性");

check(
  "★ 字段说明另起一行（不跟标签挤在同一行）",
  /\.field > label\s*\{[^}]*flex-wrap:\s*wrap/.test(css) &&
    /\.field > label \.hint\s*\{[^}]*flex:\s*1 1 100%/.test(css),
  "同一行里中文一换行就成一团，整页看着密密麻麻"
);
check("数值徽标（.val）仍留在第一行右侧", /\.field > label \.val\s*\{[^}]*order:\s*1/.test(css));
check(
  "★ 开关行的说明也另起一行（否则跟标题连成一句长句）",
  /\.switch-row > span > \.hint\s*\{[^}]*display:\s*block/.test(css)
);
check(
  "★ 卡片说明默认折成一行、可展开（.fold / .open）",
  /\.fold\s*\{[^}]*line-clamp:\s*1/.test(css) && /\.fold\.open\s*\{/.test(css) &&
    js.includes("foldCardNotes") && /classList\.add\("fold"\)/.test(js),
  "十来张卡片的解释堆在一起就是一面灰墙"
);
check(
  "★ 只有真被截断的说明才加“展开”（短说明不该出现这把标）",
  /scrollHeight > p\.clientHeight \+ 2/.test(js)
);
check(
  "★ “展开”入口是真元素（不是 ::after）",
  /\.fold-toggle\s*\{/.test(css) && js.includes('"fold-toggle"') && !/\.fold::after/.test(css),
  "用伪元素写“展开”会被 line-clamp 一行截断一起裁掉，页面上根本看不到（线上踩过）"
);
check("折叠在页面隐藏时不打“已处理”标记（否则打开设置页就再也不折了）",
  /host\.hidden\)\s*return/.test(js));
check("卡片头部与控件之间拉了分隔线", /\.card-head\s*\{[^}]*border-bottom/.test(css));

/* ─────────── 1c) 扫码登录入口必须在面板里 ─────────── */

console.log("\n▶ 静态：QQ 未登录时能在面板里直接扫码");

check("index.html 有扫码卡片容器", html.includes('id="loginCard"') && html.includes('id="loginQrImg"'));
check("设置页 NapCat 状态条有「在面板里扫码登录」入口", html.includes('id="napcatLoginBtn"'));
check("CSS：二维码用白底容器（深色主题下二维码反色就扫不出来了）",
  /\.login-qr\s*\{[^}]*background:\s*#fff/.test(css));
check("CSS：.ghost-btn 只属于面板自身（不与浏览器默认按钮撞样式）", /\.ghost-btn\s*\{/.test(css));

/* ─────────── 1d) 手机端适配 ─────────── */

console.log("\n▶ 静态：手机端适配");

check("viewport 带 viewport-fit=cover（iPhone 刘海/手势条不被盖）", /viewport-fit=cover/.test(html));
check("底部标签栏存在，且桌面端隐藏", html.includes('class="mtabbar"') && /\.mtabbar\s*\{[^}]*display:\s*none/.test(css));
check("聊天页有返回键", html.includes('id="chatBack"'));
check("★ 窄屏主从式切换：列表/聊天二选一（靠 body.m-chat-open 控制）",
  /body\.m-chat-open \.conv-pane\s*\{[^}]*display:\s*none/.test(css) &&
  /body\.m-chat-open \.chat-pane\s*\{[^}]*display:\s*flex/.test(css) &&
  /state\.chatOpen/.test(js));
check("★ 手机端输入框字号 ≥16px（否则 iOS 聚焦时会把页面放大）",
  /@media \(max-width: 760px\)[\s\S]*?input,\s*textarea,\s*select\s*\{[^}]*font-size:\s*16px/.test(css));
check("处理了安全区 env(safe-area-inset-bottom)", /env\(safe-area-inset-bottom/.test(css));
check("★ 长按也能弹会话菜单（iOS 不触发 contextmenu）", /touchstart/.test(js) && /suppressClick/.test(js));
check("点进会话会切到聊天视图", /state\.chatOpen = true/.test(js));
check("去设置页会收起聊天视图", /state\.chatOpen = false/.test(js));

/* ─────────── 1e) 表情包 ─────────── */

console.log("\n▶ 静态：表情包（自动收集 + 按语境发 + 自巡检）");

check("设置页有表情包卡片（启用 / 上限 / 候选数 / 巡检间隔 / 发送冷却）",
  html.includes('id="setEnableStickers"') && html.includes('id="setStickerMax"') &&
  html.includes('id="setStickerCandidates"') && html.includes('id="setStickerCurate"') &&
  html.includes('id="setStickerCooldown"'));
check("模型三项改成面板可改（不再是 readonly 输入框）",
  !/id="setBaseUrl"[^>]*readonly/.test(html) && !/id="setModel"[^>]*readonly/.test(html) &&
  html.includes('id="setApiKey"') && /<input[^>]*type="password"[^>]*id="setApiKey"|<input[^>]*id="setApiKey"[^>]*type="password"/.test(html) &&
  html.includes('id="clearApiKey"'));

check("设置页有戳一戳卡片（启用 / 冷却 / 当前心情 / 心情保留）",
  html.includes('id="setEnablePoke"') && html.includes('id="setPokeCooldown"') &&
  html.includes('id="setMood"') && html.includes('id="setMoodTtl"'));
check("有表情包库弹层 + 手动入口（巡检 / 导入）",
  html.includes('id="stickerModal"') && html.includes('id="stickerGrid"') &&
  html.includes('id="stickerCurateNow"') && html.includes('id="stickerImportAlbum"'));
check("★ 面板用的是库接口而不是自带一套存储",
  js.includes('"/api/stickers"') && js.includes('/delete') && js.includes('/api/stickers/import'));
check("库图片走带令牌的地址（<img> 不能带自定义头）",
  /withToken\(`\/api\/stickers\//.test(js));
check("CSS：缩略图白底（透明 PNG 看得清）", /\.sticker-cell\s*\{[^}]*background:\s*#ffffff/.test(css));
check("★ 轮询不能因为标签页隐藏就停（否则切到手机去扫时二维码会停在旧的那张）",
  !/document\.hidden\s*\)\s*return/.test(js), "轮询里又出现了 document.hidden 提前 return");
check("回到标签页时立即对一次二维码状态", /visibilitychange/.test(js));
check("★ 二维码要显示“多久前更新/可能已过期”，否则死码与活码长得一样",
  /可能已过期/.test(js) && /ageSeconds/.test(js));

/* ─────────── 2) 回填覆盖检查（核心） ─────────── */

console.log("\n▶ 静态：保存字段必须都在加载时回填");

// 从 saveSettings 的 payload 字面量里提取 “字段名 -> 读取的 DOM id”
const saveBody = js.slice(js.indexOf("async function saveSettings"), js.indexOf("/* ─────────── 日志"));
const saveFields = [...saveBody.matchAll(/(\w+):\s*(Number\()?\$\("([A-Za-z0-9_]+)"\)\.(value|checked)/g)]
  .map((m) => ({ key: m[1], numeric: !!m[2], id: m[3], prop: m[4] }));

// loadSettings 里被赋值（回填）的 id
const loadBody = js.slice(js.indexOf("async function loadSettings"), js.indexOf("async function saveSettings"));
const filledIds = new Set([...loadBody.matchAll(/\$\("([A-Za-z0-9_]+)"\)\.(?:value|checked)\s*=/g)].map((m) => m[1]));

check(
  "★ 设备行标出目录来源（设备专属 / 全局默认 / 桥自报）",
  /"设备专属"/.test(js) && /"全局默认"/.test(js) && /"桥自报"/.test(js),
  "设备行必须写清这个工作目录是哪来的：只写个目录名时，改全局目录会让人以为没生效"
);
check(
  "能从 saveSettings 解析出待保存字段",
  saveFields.length > 0,
  `解析到 ${saveFields.length} 个`
);

// 设备表（模型/目录/工具/超时/启用）不在 DOM-id 那套字段里，单独盯一眼：
// 曾经这条漏写 → 在面板里改设备配置点保存完全没用（服务器还是旧值，号主报过）。
check(
  "★ 保存请求带上设备表 agentDevices（没带 = 面板改设备配置白改）",
  /payload\.agentDevices\s*=\s*JSON\.stringify\(\s*agentDevices/.test(saveBody),
  "saveSettings 的 payload 里没有 agentDevices"
);
check(
  "★ 设备表未拉取成功时不许回写（防止一次保存把设备配置清空）",
  /if \(agentDevicesLoaded\)/.test(saveBody),
  "回写 agentDevices 前必须先判断 agentDevicesLoaded"
);
// 设备表状态必须声明在**外层作用域**：填表在 bindUi() 里、读表在 saveSettings() 里，
// 写在 bindUi 内部时 saveSettings 会直接 ReferenceError（实测踩过：保存按钮静默失效）。
const topLevelDeviceLet = /^ {2}let agentDevices = \[\];$/m.test(js) && /^ {2}let agentDevicesLoaded = false;$/m.test(js);
check(
  "★ 设备表状态声明在外层作用域（写在 bindUi() 里 = 保存直接 ReferenceError）",
  topLevelDeviceLet,
  "agentDevices / agentDevicesLoaded 必须是顶层 let（缩进两格），不能在 bindUi() 内部"
);
// 同一类坑：saveSettings() 在外层，bindUi() 内部的函数它看不见。
// 直接调 refreshAgentDevicesFull() 会 ReferenceError → 被 catch 成“保存失败”（其实已保存）。
check(
  "★ saveSettings 不直接调用 bindUi() 内部的函数（要走走 afterSettingsSaved 钩子）",
  !/refreshAgentDevicesFull\s*\(/.test(saveBody) && /afterSettingsSaved/.test(saveBody),
  "saveSettings 里直接引用了 refreshAgentDevicesFull（定义在 bindUi 内部）—— 请改成 afterSettingsSaved 钩子"
);
const notFilled = saveFields.filter((f) => !filledIds.has(f.id));
check(
  `待保存的 ${saveFields.length} 个字段全部在 loadSettings 中回填`,
  notFilled.length === 0,
  notFilled.map((f) => `${f.key}(${f.id})`).join(", ")
);

/* ─────────── 3) 动态：真跑一遍 ─────────── */

console.log("\n▶ 动态：启动 → 进设置页 → 保存");

const domReady = [];
const navItems = [];
const mtabItems = [];
const navClicks = [];
const saveClicks = [];
const classCalls = [];   // 记录 classList 上的调用，用来验证手机端视图状态的初始化

const handlers = new Map();
function fire(id, type, ev) {
  const list = handlers.get(`${id}:${type}`) || [];
  for (const fn of list) fn(ev || { target: { id } });
}

function makeEl(id, tag = "div") {
  return {
    id,
    tagName: tag.toUpperCase(),
    value: "",
    checked: false,
    textContent: "",
    innerHTML: "",
    hidden: false,
    disabled: false,
    className: "",
    src: "",
    alt: "",
    title: "",
    scrollTop: 0,
    scrollHeight: 100,
    clientHeight: 100,
    dataset: {},
    style: {},
    children: [],
    classList: {
      add(c) { classCalls.push(["add", c]); },
      remove(c) { classCalls.push(["remove", c]); },
      toggle(c, on) { classCalls.push(["toggle", c, on]); },
      contains: () => false
    },
    addEventListener(type, fn) {
      const key = `${id}:${type}`;
      if (!handlers.has(key)) handlers.set(key, []);
      handlers.get(key).push(fn);
      if (id === "saveBtn" && type === "click") saveClicks.push(fn);
      if ((id.startsWith("nav-") || id.startsWith("mtab-")) && type === "click") {
        navClicks.push({ page: id.replace(/^(nav|mtab)-/, ""), fn });
      }
    },
    removeEventListener() {},
    appendChild(c) { this.children.push(c); return c; },
    insertBefore(c) { this.children.unshift(c); return c; },
    replaceChildren() { this.children = []; },
    querySelector: () => makeEl(id + ":child"),
    querySelectorAll: () => [],
    focus() {},
    remove() {},
    closest: () => null,
    getAttribute: () => null,
    setAttribute() {},
    scrollIntoView() {}
  };
}

const elCache = new Map();
const document = {
  getElementById(id) {
    if (!htmlIds.has(id)) return undefined; // 与真浏览器一致：取不到就是 undefined
    if (!elCache.has(id)) elCache.set(id, makeEl(id));
    return elCache.get(id);
  },
  querySelector: () => null,
  querySelectorAll(sel) {
    // app.js 用 ".navitem"（桌面左栏）或 ".navitem, .mtab"（含手机底部标签栏）
    if (sel !== ".navitem" && sel !== ".navitem, .mtab") return [];
    if (navItems.length === 0) {
      for (const page of ["chat", "settings"]) {
        const b = makeEl("nav-" + page);
        b.dataset = { page };
        navItems.push(b);
      }
      for (const page of ["chat", "settings"]) {
        const b = makeEl("mtab-" + page);
        b.dataset = { page };
        mtabItems.push(b);
      }
    }

    return sel === ".navitem" ? navItems : navItems.concat(mtabItems);
  },
  createElement: (t) => makeEl("created", t),
  createDocumentFragment: () => makeEl("frag"),
  hidden: false,
  addEventListener(type, fn) { if (type === "DOMContentLoaded") domReady.push(fn); },
  removeEventListener() {},
  body: makeEl("body"),
  documentElement: makeEl("html")
};

const RUNTIME = {
  botPersona: "老群友", messageWhitelist: "123,456", aiDesire: 50, suitabilityThreshold: 10,
  aiModeEnabled: true, maxTokens: 4096, groupCooldownSeconds: 8, privateCooldownSeconds: 3,
  idleFallbackSeconds: 60, splitReplies: true, segmentDelayMs: 700, maxContextMessages: 200,
  profileLookupCount: 8, profileSummaryLines: 8, maxProfileChars: 1200,
  maxMessagesPerConversation: 500, maxConcurrentReplies: 2, enableProfileSummary: true,
  profileSummaryThreshold: 20, profileSummaryMaxChars: 160, profileSummaryIntervalSeconds: 120,
  enableStickers: true, stickerLibraryMax: 120, stickerCandidates: 6, stickerCurateIntervalSeconds: 3600,
  stickerCooldownSeconds: 120, enablePoke: true, pokeCooldownSeconds: 45, mood: "", moodTtlSeconds: 7200,
  healthReportEnabled: true, healthReportTime: "18:00", healthReportTargets: "10001",
  // 脱敏开关 + Agent 附加提示词（面板可改；默认那份是隐私红线）
  enableAgentMask: true, agentPrompt: "【隐私红线】不要读取群聊正文"
};
const ENV = {
  modelBaseUrl: "http://x/v1", modelBaseUrlSource: "env",
  model: "m", modelSource: "panel",
  apiKeyMasked: "sk-1****", apiKeySet: true, apiKeySource: "panel",
  oneBotProtocol: "ForwardWebSocket",
  oneBotAddress: "ws://napcat:3001", oneBotTokenMasked: "", uin: "10001", dataDir: "/data",
  healthPort: 8080, tz: "Asia/Shanghai"
};

const calls = [];

// 设备表：这是**服务端那份**。面板保存时整个表会被替换 —— 所以“没拉到就回写”= 一次点保存就删光设备配置。
let serverDevices = [];
let agentStatusFails = false;      // 模拟“设备表拉不到”
let settingsDelayMs = 0;           // 模拟保存请求在飞（用来看保存期间的新编辑会不会被回填盖掉）
let agentStatusHits = 0;
const deviceNames = () => serverDevices.map((d) => d.name);
const statusPayload = {
  status: {
    onebot: { connected: true, protocol: "ForwardWebSocket" },
    account: { uin: "10001", selfId: 10001, online: false },
    agent: { enabled: true, model: "mock" }
  },
  aiMode: true,
  conversations: []
};
const qrPayload = {
  ok: true, configured: true,
  url: "https://txz.qq.com/p?k=TESTKEY&f=1600001615",
  ageSeconds: 4, key: "abc123def456", error: null
};
const fetchStub = async (url, opts) => {
  const target = String(url);
  const method = (opts && opts.method) || "GET";
  calls.push({ url: target, method, body: opts && opts.body });
  let payload = statusPayload;
  if (target.includes("/api/agent/status")) {
    agentStatusHits++;
    if (agentStatusFails) throw new Error("synthetic agent status failure");
    payload = {
      enabled: true, hostAgent: true, serverAgent: false, prefix: "//", target: "auto",
      connected: serverDevices.some((d) => d.online),
      devices: deviceNames(), deviceList: serverDevices.map((d) => ({ ...d })),
      deviceModels: ["provider/model-a"], globalWorkdir: "C:/synthetic/global",
      summary: "synthetic", tokenConfigured: true, allowedUsers: "10001"
    };
  } else if (target.includes("/api/settings")) {
    if (method === "POST" && settingsDelayMs) await new Promise((r) => setTimeout(r, settingsDelayMs));
    if (method === "POST" && opts && opts.body) {
      const sent = JSON.parse(opts.body);
      if (typeof sent.agentDevices === "string") {
        // 服务端与 AppSettings 一致：整表替换（只存配置字段，online/models 是算出来的）
        serverDevices = JSON.parse(sent.agentDevices).map((d) => ({ ...d, online: false, models: [] }));
      }
    }
    payload = { runtime: RUNTIME, env: ENV, settingsFile: "/data/data/settings.json" };
  } else if (target.includes("/api/qqlogin")) {
    payload = qrPayload;
  } else if (target.includes("/api/health-report")) {
    // 健康日报：GET 拿状态、POST 拿正文（预览/真发都走同一个接口）
    payload = method === "POST"
      ? { ok: true, mode: "preview", text: "🩺 服务器健康日报 · 测试" }
      : {
          enabled: true, time: "18:00", targets: "10001", targetList: [10001],
          nextRunAt: "2026-09-19T18:00:00+08:00", lastSentAt: null, lastError: null, sentCount: 0
        };
  } else if (target.includes("/api/logs")) {
    // 面板首屏会拉日志历史（刷新页面后不再空白）
    payload = { lines: [
      { time: 1700000000000, text: "[Agent] 历史日志-A" },
      { time: 1700000001000, text: "[Agent] 历史日志-B" }
    ] };
  }
  return { ok: true, status: 200, text: async () => JSON.stringify(payload) };
};

const store = new Map();
let confirmAnswer = true;
const sandbox = {
  document,
  location: { href: "http://127.0.0.1:8080/?token=test-token", reload() {}, pathname: "/", search: "?token=test-token", hash: "" },
  history: { replaceState() {} },
  localStorage: { getItem: (k) => (store.has(k) ? store.get(k) : null), setItem: (k, v) => store.set(k, v) },
  fetch: fetchStub,
  EventSource: class { constructor() {} addEventListener() {} },
  URL, URLSearchParams, console, setTimeout, clearTimeout,
  setInterval: (fn, ms) => setTimeout(fn, ms),
  clearInterval: (id) => clearTimeout(id),
  requestAnimationFrame: (fn) => setTimeout(fn, 0),
  confirm: () => confirmAnswer,
  prompt: () => null,
  alert: () => {},
  matchMedia: () => ({ matches: false, addEventListener() {}, addListener() {}, removeEventListener() {} }),
  JSON, Date, Math, Object, Array, String, Number, Boolean, Map, Set, Promise, RegExp, Error
};
sandbox.window = sandbox;
sandbox.globalThis = sandbox;

let loadError = null;

// 给沙箱跑的那份加一个探针出口：真跑一遍时要能从外面看/改设备表草稿。
// 注意用的是 jsRun 而不是 js —— 静态检查仍然扫原文，不能因为测试而改动被测文件。
const probeMarker = 'document.addEventListener("DOMContentLoaded", boot);';
const jsRun = js.replace(probeMarker, `globalThis.probe = {
  loadSettings, saveSettings, markSettingsDirty,
  deviceDraft: () => agentDevices.map((d) => ({ ...d })),
  deviceLoaded: () => agentDevicesLoaded,
  editDevice: (i, patch) => { if (agentDevices[i]) Object.assign(agentDevices[i], patch); if (typeof agentDevicesEdited !== "undefined") agentDevicesEdited = true; markSettingsDirty(); },
  setDraft: (rows) => { agentDevices = rows.map((d) => ({ ...d })); if (typeof agentDevicesEdited !== "undefined") agentDevicesEdited = true; markSettingsDirty(); },
  forgetDevices: () => { agentDevicesLoaded = false; if (typeof agentDevicesSaved !== "undefined") agentDevicesSaved = []; },
  isDirty: () => settingsDirty
};
${probeMarker}`);
check("★ 探针出口注入成功（注不进去的话下面设备表的动态检查全是假的）",
  jsRun !== js && jsRun.includes("globalThis.probe"));

try {
  vm.createContext(sandbox);
  vm.runInContext(jsRun, sandbox, { filename: "app.js" });
} catch (e) {
  loadError = `${e.name}: ${e.message}`;
}
check("app.js 能无异常加载", loadError === null, loadError);

let bootError = null;
for (const fn of domReady) {
  try { await fn(); } catch (e) { bootError = `${e.name}: ${e.message}`; }
}
await new Promise((r) => setTimeout(r, 400));
check("boot() 无异常", bootError === null, bootError);
check("注册了保存按钮的点击处理", saveClicks.length === 1, `实际 ${saveClicks.length} 个`);

// 日志面板的历史回填（以前日志只活在浏览器内存里：一刷新页面就空白 —— 号主反馈）
check("app.js 会去拉 /api/logs（首屏历史）", js.includes('/api/logs'));
check("boot 真的请求了 /api/logs", calls.some((c) => c.method === "GET" && c.url.includes("/api/logs")),
  calls.map((c) => c.url).join(" | "));
check("★ 历史日志被渲染进日志面板（刷新后不再空白）",
  (document.getElementById("logBox")?.innerHTML || "").includes("历史日志-A"),
  (document.getElementById("logBox")?.innerHTML || "(空)").slice(0, 120));

// 日志很长时要能一键到顶 / 到底（号主要求的两个按钮）
check("app.js 绑定了日志“顶部 / 底部”两个按钮", js.includes('"logTopBtn"') && js.includes('"logBottomBtn"'));
{
  const box = document.getElementById("logBox");
  box.scrollTop = 40;
  fire("logTopBtn", "click");
  check("★ 点“顶部”滚到最上面", box.scrollTop === 0, `scrollTop=${box.scrollTop}`);
  fire("logBottomBtn", "click");
  check("★ 点“底部”滚到最下面", box.scrollTop === box.scrollHeight,
    `scrollTop=${box.scrollTop} scrollHeight=${box.scrollHeight}`);
}
check("★ boot() 会把手机端视图初始化成列表（body.m-chat-open）",
  classCalls.some((c) => c[0] === "toggle" && c[1] === "m-chat-open"),
  JSON.stringify(classCalls.slice(0, 6)));

// ─────────── 本机 Agent 卡片（// 命令，handoff-4 §31）───────────
check("面板有本机 Agent 卡片（开关 / 可用 QQ / 前缀 / 测试按钮）",
  ["setEnableAgentBridge", "setAgentAllowedUsers", "setAgentPrefix", "setAgentWorkDir",
   "agentTestGo", "agentStatusGo", "agentTestPrompt", "agentOut"]
    .every((id) => html.includes(`id="${id}"`)),
  ["setEnableAgentBridge", "setAgentAllowedUsers", "agentTestGo"]
    .filter((id) => !html.includes(`id="${id}"`)).join(", ") || "都在");
check("app.js 把 agent 设置读进表单（渲染）/ 写回保存体",
  js.includes("setEnableAgentBridge") && js.includes("enableAgentBridge:") &&
  js.includes("agentAllowedUsers:") && js.includes("agentPrefix:"));
check("app.js 绑定了“送到本机跑一下 / 看连接状态”两个按钮",
  js.includes('$("agentTestGo").addEventListener') && js.includes('$("agentStatusGo").addEventListener'));

// ─────────── Agent 会话（自动标题 / 总览 / 新建切换删除）───────────
check("面板有 Agent 会话区（聊天下拉 / 新建 / 刷新 / 列表）",
  ["agentSessionChat", "agentSessionNew", "agentSessionRefresh", "agentSessionTable"]
    .every((id) => html.includes(`id="${id}"`)),
  ["agentSessionChat", "agentSessionNew"]
    .filter((id) => !html.includes(`id="${id}"`)).join(", ") || "都在");
check("★ app.js 实现了会话列表/切换/改名/删除（与群里同一套 API）",
  js.includes("async function refreshAgentSessions") &&
  js.includes('/api/agent/sessions') &&
  js.includes('action: "rename"') && js.includes('action: "delete"') && js.includes('action: "use"'));
check("★ 会话总览（全部聊天 + 总数）在面板里可见",
  js.includes('全部聊天（共') && js.includes("会话总数："));
check("面板引用了 /api/agent/sessions 且 /api/agent/setup 走带令牌的地址",
  js.includes("withToken(`${apiBase()}/api/agent/setup") || js.includes("/api/agent/setup"));

// ─────────── 列出会话时脱敏 + Agent 附加提示词（默认：不读取敏感信息）───────────
check("面板有「列出会话时脱敏」开关（群名/昵称/QQ 号只留前 3 后 2）",
  html.includes('id="setAgentMask"') && js.includes('$("setAgentMask").checked') &&
  js.includes("enableAgentMask:"),
  !js.includes("enableAgentMask:") ? "saveSettings 没把开关发出去（勾了不生效）" : "");
check("脱敏开关默认开：旧配置（没这个字段）回填后仍是勾上的",
  js.includes("r.enableAgentMask !== false"));
check("面板有「Agent 附加提示词」输入框 + 恢复默认按钮",
  html.includes('id="setAgentPrompt"') && html.includes('id="agentPromptReset"') &&
  js.includes('$("setAgentPrompt").value') && js.includes("agentPrompt:"),
  "默认那份（不读取敏感信息）要写进每个 // 任务，所以面板必须能改");
check("★ 默认提示词由服务端下发（前端不抄一份，免得两处漂移）",
  js.includes("agentPromptDefault") && !js.includes("【隐私红线（优先级最高）】"));
check("★ 改名输入框填真名（nameRaw）：脱敏开启时拿占位符去改名会把「群友A」写回去",
  js.includes("old.nameRaw || old.name"));
check("★ 会话/聊天都带序号（面板的顺序 = 群里 //sessions 的顺序，//use 序号能对上）",
  js.includes("${i + 1}) ") && js.includes("#${i + 1}") && js.includes("${i + 1}. ${c.name || k}"),
  "没序号的话，面板上看到第几个、群里 //use 第几个就对不上");

// ─────────── 服务器健康日报（定时私聊推送）───────────
check("面板有健康日报卡片（开关 / 时刻 / 收件人 / 预览 / 立即发 / 状态提示）",
  ["setHealthReportEnabled", "setHealthReportTime", "setHealthReportTargets",
   "healthReportPreviewGo", "healthReportSendGo", "healthReportHint", "healthReportOut"]
    .every((id) => html.includes(`id="${id}"`)),
  ["setHealthReportEnabled", "setHealthReportTime", "setHealthReportTargets",
   "healthReportPreviewGo", "healthReportSendGo"].filter((id) => !html.includes(`id="${id}"`)).join(", ") || "都在");
check("★ 推送时刻用 type=time（面板上直接选点，不用手敲冒号）",
  html.includes('type="time" id="setHealthReportTime"'));
check("★ app.js 在**顶层**实现了 runHealthReport / refreshHealthReport",
  /^\s{2}async function refreshHealthReport\(\)/m.test(js) && /^\s{2}async function runHealthReport\(/m.test(js),
  "定义必须是 2 空格缩进的顶层函数 —— 藏在别的函数体里 loadSettings() 会 ReferenceError");
check("健康日报调的是面板自己的接口（浏览器不直连 QQ）",
  js.includes('api("/api/health-report"') && js.includes("JSON.stringify({ mode })"));

/* ─────────── 3b) 动态：扫码登录卡片 ─────────── */

console.log("\n▶ 动态：QQ 未登录时必须能直接在面板里扫码");

const loginCard = document.getElementById("loginCard");
const loginImg = document.getElementById("loginQrImg");

check("未登录时扫码卡片自动出现", loginCard.hidden === false);
check("★ 二维码来自面板自己的接口（浏览器不直连 NapCat：跨域 + 另一道认证）",
  String(loginImg.src).includes("/api/qqlogin/qrcode.svg"), String(loginImg.src));
check("★ 二维码地址必须带令牌（<img> 不能带自定义头，令牌只能走查询参数；漏了它就只在生产环境挂）",
  String(loginImg.src).includes("token=test-token"), String(loginImg.src));
check("二维码按指纹做缓存键（同一张图不重载，否则扫到一半会闪）",
  String(loginImg.src).includes("k=abc123def456"), String(loginImg.src));
check("卡片给出可复制的二维码链接（扫不动时兜底）",
  String(document.getElementById("loginUrl").textContent).includes("txz.qq.com"),
  document.getElementById("loginUrl").textContent);

// 账号上线 → 卡片必须自己消失（别让用户以为还掉线）
statusPayload.status.account.online = true;
for (const fn of domReady) {
  try { await fn(); } catch (e) { /* 同一次 boot，上面的断言已经覆盖 */ }
}
await new Promise((r) => setTimeout(r, 200));
check("★ 账号上线后扫码卡片自动消失", loginCard.hidden === true);

// 进设置页（触发 loadSettings 回填）
let navError = null;
const nav = navClicks.find((n) => n.page === "settings");
if (nav) {
  try { nav.fn({}); } catch (e) { navError = `${e.name}: ${e.message}`; }
}
await new Promise((r) => setTimeout(r, 400));
check("进入设置页无异常", navError === null, navError);

const readField = (f) => {
  const el = document.getElementById(f.id);
  if (el === undefined) return undefined;
  if (f.prop === "checked") return el.checked;
  return f.numeric ? Number(el.value) : String(el.value);
};

// 每个字段读出来的值，必须等于服务端刚返回的值（这才是“回填正确”的真正定义）
const wrongFill = saveFields.filter((f) => {
  if (RUNTIME[f.key] === undefined) return false; // env 类字段不在 runtime 里
  return readField(f) !== RUNTIME[f.key];
});
check(
  `回填后 ${saveFields.filter((f) => RUNTIME[f.key] !== undefined).length} 个字段与服务器值一致`,
  wrongFill.length === 0,
  wrongFill.map((f) => `${f.key}: 表单=${JSON.stringify(readField(f))} 服务器=${JSON.stringify(RUNTIME[f.key])}`).join("; ")
);
// 保存
const before = calls.length;
// 先把成功提示清空：否则它可能是上游某一步留下的旧值，这条检查就形同虚设
const barEl = document.getElementById("saveBarText");
if (barEl) barEl.textContent = "";
try { await saveClicks[0]({}); } catch (e) { console.log("    （保存点击抛错：" + (e && e.message) + "）"); }
await new Promise((r) => setTimeout(r, 300));

const saveCall = calls.slice(before).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
check("点击保存确实发出了 POST /api/settings", !!saveCall);

// 保存流程不能自己抛错：曾经 refreshAgentDevicesFull 跨作用域调用 → ReferenceError，
// 被 catch 成“保存失败”（服务器其实存上了），靠 saveBarText 这句话也能看出来。
check(
  "★ 点保存后提示的是“已保存”（不是“保存失败”）",
  String(document.getElementById("saveBarText")?.textContent || "").includes("已保存"),
  String(document.getElementById("saveBarText")?.textContent || "(保存提示为空 = 保存过程抛错了)")
);

if (saveCall) {
  const payload = JSON.parse(saveCall.body);
  // 设备表 agentDevices 是**额外**字段（不在 DOM-id 那套里），不算进“表单字段数”。
  const declaredKeys = Object.keys(payload).filter((k) => k !== "agentDevices");
  check("payload 字段数与表单一致（设备表算额外字段）", declaredKeys.length === saveFields.length,
    `${declaredKeys.length} vs ${saveFields.length}`);

  // 核心不变式：**什么都不改直接保存，payload 必须与服务端当前值完全一致**。
  // 一旦有字段没被回填，它就会以 0/空/false 发回来 → 服务端把它压到最小值
  // （白名单被清空则直接进入严格模式并删光会话）。
  const drifted = Object.entries(payload).filter(([k, v]) => RUNTIME[k] !== undefined && v !== RUNTIME[k]);
  check(
    "★ 不改动直接保存，payload 与服务端值完全一致（没被空值污染）",
    drifted.length === 0,
    drifted.map(([k, v]) => `${k}: 发出=${JSON.stringify(v)} 服务器=${JSON.stringify(RUNTIME[k])}`).join("; ")
  );

  check("payload 携带了真实人设", payload.botPersona === RUNTIME.botPersona, String(payload.botPersona));
  check("payload 携带了真实白名单", payload.messageWhitelist === RUNTIME.messageWhitelist, String(payload.messageWhitelist));
  check("payload 携带了真实 maxTokens", payload.maxTokens === RUNTIME.maxTokens, String(payload.maxTokens));
}

/* ─────────── 4) 服务器健康日报（定时私聊推送） ─────────── */

console.log("\n▶ 动态：服务器健康日报（预览 / 立即发 / 状态提示）");

check("★ loadSettings 之后 /api/health-report 被请求了（状态提示不是写死的）",
  calls.some((c) => c.method === "GET" && c.url.includes("/api/health-report")),
  calls.map((c) => c.url).join(" | "));
check("★ 下次推送时刻被渲染进提示（今天/明天 + 北京时间）",
  /下次推送：.*（北京时间）/.test(document.getElementById("healthReportHint")?.textContent || ""),
  document.getElementById("healthReportHint")?.textContent);

const healthBefore = calls.length;
fire("healthReportPreviewGo", "click");
await new Promise((r) => setTimeout(r, 250));
const previewCall = calls.slice(healthBefore).find((c) => c.method === "POST" && c.url.includes("/api/health-report"));
check("★ 点「预览」真的 POST 了 /api/health-report（mode=preview）", !!previewCall, previewCall?.body);
check("★ 预览正文被写进卡片（发出去之前能先看一眼）",
  (document.getElementById("healthReportOut")?.textContent || "").includes("服务器健康日报"),
  (document.getElementById("healthReportOut")?.textContent || "(空)").slice(0, 80));

/* ─────────── 4) 未保存修改的提示与拦截 ─────────── */

console.log("\n▶ 动态：未保存修改的提示与离开拦截");

const dirtyHint = document.getElementById("dirtyHint");
check("刚加载完不显示「未保存」提示", dirtyHint.hidden === true);

// 编辑一个字段 → 应出现提示
fire("pageSettings", "input", { target: { id: "setPersona" } });
check("编辑后显示「未保存」提示", dirtyHint.hidden === false);

// 有未保存修改时离开设置页 → 应被拦下
confirmAnswer = false;
fire("nav-chat", "click");
check("★ 有未保存修改时离开设置页会被拦下（不再“自动复原”）", document.getElementById("pageSettings").hidden === false);

// 确认后再离开
confirmAnswer = true;
fire("nav-chat", "click");
check("确认后可以离开", document.getElementById("pageSettings").hidden === true);

// 回到设置页（重新加载 → 提示清掉），且改主题不算未保存
fire("nav-settings", "click");
await new Promise((r) => setTimeout(r, 300));
check("回到设置页后提示被清掉", dirtyHint.hidden === true);
fire("pageSettings", "change", { target: { id: "setTheme" } });
check("改主题不会被当成未保存的修改", dirtyHint.hidden === true);

/* ─────────── 5) 外部设备表：加载 / 保存 / 放弃 / 转义 / 竞态 ─────────── */

console.log("\n▶ 动态：外部设备表（加载 / 保存 / 放弃 / 转义 / 竞态）");

// 静态：这一组修复的关键点（缺一个就会退回“改了半天没保存/新设备自己消失”）
const loadBodyFull = js.slice(js.indexOf("async function loadSettings"), js.indexOf("async function saveSettings"));
check("★ 进设置页时就拉设备表（只靠手动点刷新 = 首次进来是空表）",
  /await loadAgentDevices\(\)/.test(loadBodyFull),
  "loadSettings 里没有调 loadAgentDevices()");
check("★ 设备名/目录进 innerHTML 前要转义",
  /function escapeHtml\(/.test(js) && (js.match(/escapeHtml\(/g) || []).length >= 8,
  `escapeHtml 出现 ${(js.match(/escapeHtml\(/g) || []).length} 次`);
check("★ ensureDeviceOption 必须是顶层函数（定义在 bindUi() 内部时“指定设备 + 重进设置页”直接 ReferenceError）",
  /^ {2}function ensureDeviceOption\(name\) \{/m.test(js));
check("★ 保存期间的新编辑不被回填盖掉（改动序号门卡）",
  /settingsEditSeq/.test(saveBody) && /const seqAtSend = settingsEditSeq;/.test(saveBody),
  "saveSettings 里没有 settingsEditSeq 门卡");

const probe = sandbox.probe;
const tableHtml = () => String(document.getElementById("agentDeviceTable")?.innerHTML || "");
const selectHtml = () => String(document.getElementById("setAgentDevice")?.innerHTML || "");
const enterSettings = async () => {
  fire("nav-settings", "click");
  await new Promise((r) => setTimeout(r, 300));
};
const lastSettingsBody = () => {
  const c = [...calls].reverse().find((x) => x.method === "POST" && x.url.includes("/api/settings"));
  return c ? JSON.parse(c.body) : null;
};

// ① 指定设备（agentTarget = 设备名）：loadSettings 会走 ensureDeviceOption —— 曾经这里是 ReferenceError
RUNTIME.agentTarget = "DEV-A";
serverDevices = [{ name: "DEV-A", online: true, enable: true, model: "provider/model-a",
  workdir: "C:/synthetic/device-a", tools: "", timeoutSec: 0, models: ["provider/model-a"] }];
await enterSettings();
check("★ 指定设备时重新进设置页能跑完（不再 ReferenceError）",
  probe.deviceLoaded() === true && probe.deviceDraft().length === 1, JSON.stringify(probe.deviceDraft()));
check("★ 进设置页就把设备表填好了（不用先手动点「刷新设备」）",
  probe.deviceDraft()[0]?.name === "DEV-A" && tableHtml().includes("C:/synthetic/device-a"),
  tableHtml().slice(0, 160));
check("★ 指定设备的下拉里保留了那一项", document.getElementById("setAgentDevice").value === "DEV-A",
  String(document.getElementById("setAgentDevice").value));

// ② 新增设备 ——> 保存：新行必须进请求，且保存后不能被刷新覆盖
// （只取第一个处理函数：本探针为了验扫码卡片又跑了一遍 boot()，事件处理会被注册两次 ——
//  这是探针自己的事，app.js 里 agentDeviceAdd 只绑一次）
sandbox.prompt = () => "DEV-NEW";
const rowsBeforeAdd = probe.deviceDraft().length;
(handlers.get("agentDeviceAdd:click") || [])[0]();
check("点「添加设备」后草稿里多一行",
  probe.deviceDraft().length === rowsBeforeAdd + 1, JSON.stringify(probe.deviceDraft().map((d) => d.name)));
await saveClicks[0]({});
await new Promise((r) => setTimeout(r, 200));
const sentDevices = JSON.parse(lastSettingsBody()?.agentDevices || "[]");
check("★ 新增的设备真的进了保存请求（以前被 agentDevicesLoaded 拦下）",
  sentDevices.some((d) => d.name === "DEV-NEW"), JSON.stringify(lastSettingsBody()?.agentDevices));
check("★ 保存后服务端设备表里也有它（不再被保存后的刷新覆盖）",
  serverDevices.some((d) => d.name === "DEV-NEW"), JSON.stringify(serverDevices.map((d) => d.name)));
check("保存后草稿与服务端一致", probe.deviceDraft().length === serverDevices.length,
  `${probe.deviceDraft().length} vs ${serverDevices.length}`);

// ③ 放弃草稿：离开未保存的页面再回来，草稿必须回到服务端的值（否则下次保存会把放弃的改动一起提交）
probe.editDevice(0, { workdir: "C:/synthetic/unsaved" });
confirmAnswer = true;
fire("nav-chat", "click");
await enterSettings();
check("★ 放弃后重新进设置页，设备草稿回到服务端的值",
  probe.deviceDraft()[0]?.workdir === "C:/synthetic/device-a", JSON.stringify(probe.deviceDraft()[0]));
await saveClicks[0]({});
await new Promise((r) => setTimeout(r, 200));
check("★ 放弃后的那次保存没有提交已放弃的设备目录",
  !String(lastSettingsBody()?.agentDevices).includes("unsaved"), String(lastSettingsBody()?.agentDevices));

// ④ 转义：设备名/目录/工具白名单全部来自配置与桥上报
serverDevices = [{ name: '<b id="review-marker">literal</b>', online: false, enable: true, model: "p/m",
  workdir: 'C:/x" onfocus="alert(1)', tools: "bash,read", timeoutSec: 0, models: ["p/m"], pi: "<img src=x>" }];
await enterSettings();
const escaped = tableHtml();
check("★ 设备名转义后才进 innerHTML",
  !escaped.includes('<b id="review-marker">') && escaped.includes("&lt;b id=&quot;review-marker&quot;&gt;"),
  escaped.slice(0, 200));
check("★ 目录里的引号不能跑出 value 属性",
  !escaped.includes('onfocus="alert(1)"') && escaped.includes("C:/x&quot;"), escaped.slice(0, 200));
check("★ 桥上报的 pi 版本号也要转义", !escaped.includes("<img src=x>"), escaped.slice(0, 200));
check("★ 指定设备下拉同样转义", !selectHtml().includes('<b id="review-marker">'), selectHtml().slice(0, 160));

// ⑤ 保存请求在飞时用户又改了东西：刷新结果不能盖掉新编辑，也不能谎报“已保存”
settingsDelayMs = 250;
const inFlight = saveClicks[0]({});
await new Promise((r) => setTimeout(r, 60));
probe.editDevice(0, { tools: "late-edit" });
await inFlight;
await new Promise((r) => setTimeout(r, 200));
settingsDelayMs = 0;
check("★ 保存期间的新编辑不会被刷新结果盖掉",
  probe.deviceDraft()[0]?.tools === "late-edit", JSON.stringify(probe.deviceDraft()[0]));
check("★ 保存期间有新编辑时，未保存提示要留着（不能谎报已存下）", probe.isDirty() === true);
check("保存提示里说明了还有新修改",
  String(document.getElementById("saveBarText")?.textContent || "").includes("新的修改"),
  String(document.getElementById("saveBarText")?.textContent || "(空)"));

// ⑥ 设备表拉不到 + 用户改了设备行：既不能发半份表（服务端整表替换 = 删光其它设备），也不能无声丢掉
agentStatusFails = true;
probe.forgetDevices();
const devicesBefore = JSON.stringify(serverDevices);
probe.setDraft([{ name: "DEV-PARTIAL", online: false, enable: true, model: "", workdir: "", tools: "", timeoutSec: 0, models: [] }]);
await saveClicks[0]({});
await new Promise((r) => setTimeout(r, 200));
agentStatusFails = false;
check("★ 设备表没拉到时不发半份设备表（发了就等于把其它设备配置删光）",
  lastSettingsBody()?.agentDevices === undefined, String(lastSettingsBody()?.agentDevices));
check("★ 但保存提示要说清“设备改动没保存”（不能无声丢掉）",
  String(document.getElementById("saveBarText")?.textContent || "").includes("设备相关改动"),
  String(document.getElementById("saveBarText")?.textContent || "(空)"));
check("服务端的设备配置没被清空", JSON.stringify(serverDevices) === devicesBefore);
check("设备表请求真的失败过（否则上面三条形同虚设 —— 拉不到才该跳过）", agentStatusHits > 0, `hits=${agentStatusHits}`);

/* ─────────── 汇总 ─────────── */

console.log("");
if (failures.length === 0) {
  console.log(`通过 ${pass}，失败 0`);
  process.exit(0);
}

console.log(`通过 ${pass}，失败 ${failures.length}`);
for (const f of failures) console.log("  ✗ " + f);
process.exit(1);
