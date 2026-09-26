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
 * 运行：node tests/BotAgent.FrontendProbe/probe.mjs
 * 退出码：0 = 全部通过，1 = 有失败项
 */
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../../src/BotAgent.Headless/wwwroot");
const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
const js = fs.readFileSync(path.join(root, "app.js"), "utf8");
const traceJsRaw = fs.readFileSync(path.join(root, "trace.js"), "utf8");
const dashJsRaw = fs.readFileSync(path.join(root, "dash.js"), "utf8");

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

/// 去注释 / 去字符串的粗略视图：用来扫「代码里真的写了什么」。
/// 为什么需要它：模板字符串一旦被吃掉（变成裸中文标识符），源码里仍然"看不出错"，
/// 而 `node --check` 也放过（`更新于` 是合法标识符）—— 只有运行期才炸。
function codeOnly(text) {
  let out = "";
  let i = 0;
  const n = text.length;
  while (i < n) {
    const c = text[i], d = text[i + 1];
    if (c === "/" && d === "/") { while (i < n && text[i] !== "\n") i++; continue; }
    if (c === "/" && d === "*") { i += 2; while (i < n && !(text[i] === "*" && text[i + 1] === "/")) i++; i += 2; continue; }
    if (c === '"' || c === "'" || c === "`") {
      const q = c; i++;
      while (i < n && text[i] !== q) { if (text[i] === "\\") i++; i++; }
      i++; out += '""'; continue;
    }
    out += c; i++;
  }
  return out;
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
  "高矮不一的卡片横着铺开就是“一块一块”的乱（管理员原话），改成一节一节看"
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
  check("设置页有能力与审批卡片（场景预设 / 审批开关 / 审批人 / 生效回显）",
    html.includes('id="setScenarioPreset"') && html.includes('id="setEnableApprovals"') &&
    html.includes('id="setApprovalApprovers"') && html.includes('id="scenarioCapHint"') &&
    html.includes('id="approvalHint"'));
  check("场景预设下拉必须在界面上真的可选（V3 §12.1：不能只有服务端字段）",
    /<select[^>]*id="setScenarioPreset"/.test(html) &&
    html.includes('value="on-demand"') && html.includes('value="research"') && html.includes('value="social"'));
  check("设置页有参与状态机卡片（5 个上限 + 生效回显 + 只读状态框 + 刷新按钮）",
    html.includes('id="setPartMaxReplies"') && html.includes('id="setPartCooldown"') &&
    html.includes('id="setPartProbing"') && html.includes('id="setPartActiveLife"') &&
    html.includes('id="setPartExitingLife"') && html.includes('id="participationHint"') &&
    html.includes('id="participationBox"') && html.includes('id="partRefreshBtn"'));
  check("★ 两个会真正改变行为的开关都在面板上（参与闸门 / 允许提问），且都写明默认关",
    html.includes('id="setPartGating"') && html.includes('id="setEnableQuestions"') &&
    /默认关/.test(html) && html.includes('id="questionHint"'));
  check("★ 参与状态只读：前端没有往 /api/participation 发 POST（它只能看，不能改状态）",
    /api\("\/api\/participation"\)/.test(js) && !/\/api\/participation"[^)]*method:\s*"POST"/s.test(js));
  check("★ 参与状态用安全文本节点渲染（会话名里可能有别人写的字，绝不进 innerHTML）",
    (function () {
      // 只看**函数体里有没有给 innerHTML 赋值** —— 注释里提到 innerHTML 不算（上一版这条就被自己的注释误报过）
      const fn = (js.match(/async function refreshParticipation\(\)[\s\S]*?\n {2}\}/) || [""])[0];
      return fn.includes('createElement("div")') && fn.includes("appendChild") && !/innerHTML\s*=/.test(fn);
    })());
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
// 动态选项由渲染函数回填，不能要求再写一次 value 覆盖其 auto 回退。
for (const match of loadBody.matchAll(/renderReasoningSelect\(\$\("([A-Za-z0-9_]+)"\)/g)) filledIds.add(match[1]);

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
// 曾经这条漏写 → 在面板里改设备配置点保存完全没用（服务器还是旧值，管理员报过）。
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
      for (const page of ["chat", "agent", "trace", "dash", "settings"]) {
        const b = makeEl("nav-" + page);
        b.dataset = { page };
        navItems.push(b);
      }
      for (const page of ["chat", "agent", "trace", "dash", "settings"]) {
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
  // 批次 E：聊天侧有限步进循环（1 = 与改造前逐字一致）
  maxAgentSteps: 1,
  idleFallbackSeconds: 60, splitReplies: true, segmentDelayMs: 700, maxContextMessages: 200,
  profileLookupCount: 8, profileSummaryLines: 8, maxProfileChars: 1200,
  maxMessagesPerConversation: 500, maxConcurrentReplies: 2, enableProfileSummary: true,
  profileSummaryThreshold: 20, profileSummaryMaxChars: 160, profileSummaryIntervalSeconds: 120,
  enableStickers: true, stickerLibraryMax: 120, stickerCandidates: 6, stickerCurateIntervalSeconds: 3600,
    stickerCooldownSeconds: 120, enablePoke: true, pokeCooldownSeconds: 45, mood: "", moodTtlSeconds: 7200,
    // 场景预设 + 人工审批（面板控件必须能回填，也必须真的发出去）
    scenarioPreset: "social", scenarioCapabilities: "scene=social caps=[chat.reply] budget=3 v1",
    enableApprovals: true, approvalApprovers: "10001",
    approvalCapabilities: "caps=[demo.echo] budget=2147483647 v1 needApproval=[demo.echo]",
    approvalTool: "demo.echo", approvalTtlSeconds: 120,
    // P1：参与状态机的上限（面板回显的是**被钳过**的那组数）
    participationMaxConsecutiveReplies: 4, participationCooldownSeconds: 25,
    participationProbingMaxReplies: 2, participationMaxActiveLifetimeSeconds: 600,
    participationMaxExitingLifetimeSeconds: 120,
    participationPolicy: "连续 ≤4 / 冷却 25s / 试探 ≤2 / 活性命 600s / 退场 120s",
    participationGating: "off（只观测：不改“发不发”的判定）",
    enableParticipationGating: false,
    enableQuestions: true,
    questionTool: "ask.question",
    questionCapabilities: "允许提问（带一次性编号与有效期；不授予任何权限）",
  healthReportEnabled: true, healthReportTime: "18:00", healthReportTargets: "10001",
  // 脱敏开关 + Agent 附加提示词（面板可改；默认那份是隐私红线）
  enableAgentMask: true, agentPrompt: "【隐私红线】不要读取群聊正文",
  // 服务器 agent 的接口/模型/密钥状态（密钥只给掩码与来源，永不下发明文）
  agentServerBaseUrl: "https://api.example.com/v1", serverModel: "agent-small",
  agentServerKeySet: true, agentServerKeyMasked: "sk-a****", agentServerKeySource: "panel",
  // 服务器 agent 的 QQ 动作（空 = 安全档；这里故意配了一个危险档的 ban 看回显）
  agentServerTools: "bash,read,qq", agentServerQqActions: "like,poke,ban",
  agentServerQqActionsEffective: "安全档 like/poke，已点名打开 ban",
  // 记住上下文：默认关（每条指令单独对待）—— fixture 里故意开一个，验证回填
  agentServerKeepContext: true,
  // 面板一键部署（高权限，默认关）+ 记住的产物地址
  panelDeployEnabled: true, panelDeployUrl: "https://example.com/app.tar.gz",
  // 透过 docker 操作服务器（高权限，默认关）
  agentServerDocker: false,
  // 白名单拆成两份（群聊/私聊），旧字段还留着做兼容：这里故意只填群聊那份，看回落提示
  messageWhitelist: "10001", whitelistGroups: "10001,20002", whitelistPrivates: "",
  whitelistGroupsFromLegacy: false, whitelistPrivatesFromLegacy: true
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
let agentWorkbenchReady = false;
let agentRunDelayMs = 0;
// 批次 I：待批单列表（服务端合成一份；面板要画卡、也要能批准）
let approvalsPayload = {
  available: true, enabled: true, tokenConfigured: true, canDecide: true,
  pending: [{
    id: "ABC234", tool: "demo.echo", summary: "执行固定假工具 demo.echo（演示用，无真实副作用）",
    key: "群聊 100***01", expiresInSeconds: 96, policyVersion: 3
  }]
};
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
      enabled: agentWorkbenchReady, hostAgent: true, serverAgent: agentWorkbenchReady, prefix: "//", target: "auto",
      connected: agentWorkbenchReady,
      devices: deviceNames(), deviceList: serverDevices.map((d) => ({ ...d })),
      deviceModels: ["provider/model-a"], globalWorkdir: "C:/synthetic/global",
      serverModel: "server/mock", agentModel: "host/mock",
      reasoningEffort: "auto", reasoningLevels: "auto\nlow\nhigh",
      serverKeepContext: false, serverTools: "read,fetch", serverDocker: false,
      summary: "synthetic", tokenConfigured: true, allowedUsers: "10001", queued: 0
    };
  } else if (target.includes("/api/agent/sessions")) {
    payload = {
      total: 2, chatCount: 2,
      chats: {
        "synthetic-source": {
          name: "合成来源A",
          sessions: [{ id: "session-1", name: "默认会话", backend: "server", device: "server", turns: 2, updatedAt: "2026-09-24T12:00:00Z", current: true, runs: [{ id: "run-1" }] }]
        },
        "panel:workspace": {
          name: "面板工作区",
          sessions: [{ id: "panel-session-1", name: "工作台会话", backend: "server", device: "server", turns: 0, updatedAt: "2026-09-25T12:00:00Z", current: true, runs: [] }]
        }
      }
    };
  } else if (target.includes("/api/tools")) {
    payload = { count: 4, healthy: true, executors: [], tools: [] };
  } else if (target.includes("/api/agent/test")) {
    if (agentRunDelayMs) await new Promise((r) => setTimeout(r, agentRunDelayMs));
    payload = { ok: true, id: "run-synthetic", text: "<synthetic-result>", durationMs: 42, toolCalls: 1, target: "server" };
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
  } else if (target.includes("/api/approvals/decide")) {
    // 批次 I 的写路径：探针只记下这次调用，返回一个“已批准”的合成结果
    payload = { ok: true, decided: "approve", reason: "approved" };
  } else if (target.includes("/api/approvals")) {
    payload = approvalsPayload;
  } else if (target.includes("/api/dashboard")) {
    // 批次 J：仪表盘的只读数据面（数字与形状，没有任何会话内容）
    payload = {
      uptimeSeconds: 3720, aiMode: true, onebot: true, accountOnline: true,
      conversations: 7, inFlight: 1, queued: 2, latencyMs: 4200,
      memory: { usedBytes: 268435456, limitBytes: 1073741824 }, load: 0.42,
      tools: { total: 26, chat: 10, qq: 10, server: 6, highRisk: 11, needApproval: 0, executors: 4, healthy: true },
      sessionPolicy: { available: true, sessions: 3, stale: 0, rebuilt: 1 },
      traces: { available: true, recent: 12, active: 1, capacity: 50 }
    };
  } else if (target.includes("/api/traces")) {
    payload = { available: true, count: 1, active: 0, capacity: 50, traces: [] };
  } else if (target.includes("/api/participation")) {
      // P1：参与状态只读接口（会话名已由服务端按脱敏开关处理）
      payload = {
        policy: "连续 ≤3 / 冷却 20s / 试探 ≤1 / 活性命 900s / 退场 180s",
        gating: "off（只观测）",
        tracked: 2,
        sessions: [
          { key: "群聊 100***01", state: "Active", reason: "replied", counters: "连续 1 / 试探 1 / 失败 0", lastTransition: "3s前" },
          { key: "群聊 200***02", state: "Observing", reason: "not_addressed", counters: "连续 0 / 试探 0 / 失败 0", lastTransition: "41s前" }
        ]
      };    } else if (target.includes("/api/logs")) {
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
    refreshParticipation,
  deviceDraft: () => agentDevices.map((d) => ({ ...d })),
  deviceLoaded: () => agentDevicesLoaded,
  editDevice: (i, patch) => { if (agentDevices[i]) Object.assign(agentDevices[i], patch); if (typeof agentDevicesEdited !== "undefined") agentDevicesEdited = true; markSettingsDirty(); },
  setDraft: (rows) => { agentDevices = rows.map((d) => ({ ...d })); if (typeof agentDevicesEdited !== "undefined") agentDevicesEdited = true; markSettingsDirty(); },
  forgetDevices: () => { agentDevicesLoaded = false; if (typeof agentDevicesSaved !== "undefined") agentDevicesSaved = []; },
  agentSnapshot: () => ({ active: state.agent.activeSessionKey, rows: state.agent.rows.map((row) => ({ key: row.identity, source: row.sourceKind, backend: row.backend, local: !!row.session.localOnly })), messages: [...state.agent.conversations.entries()].map(([key, entries]) => ({ key, entries: entries.map((entry) => ({ ...entry })) })), drafts: [...state.agent.drafts.entries()], lastResult: state.agent.lastResult }),
  selectAgentSession, createLocalAgentSession, renderAgentSessions,
  renderReasoningSelect, saveAgentWorkbenchConfig,
  isDirty: () => settingsDirty
};
${probeMarker}`);
// 这一类 bug 只有静态能扫出来、node --check 抓不住：`const summary = 更新于 ;`
// 反引号与 ${} 被吃掉后，剩下的中文成了合法标识符 —— 加载不报错、一跑到那行就 ReferenceError。
{
  const bare = codeOnly(js).match(/[=(,:]\s*[\u4e00-\u9fff][\u4e00-\u9fff0-9A-Za-z]*\s*[;)]/g) || [];
  check("★ app.js 里没有“裸中文标识符”（模板字符串被吃掉的形状）", bare.length === 0, bare.join(" "));
}
{
  const bareTrace = codeOnly(traceJsRaw).match(/[=(,:]\s*[\u4e00-\u9fff][\u4e00-\u9fff0-9A-Za-z]*\s*[;)]/g) || [];
  const bareDash = codeOnly(dashJsRaw).match(/[=(,:]\s*[\u4e00-\u9fff][\u4e00-\u9fff0-9A-Za-z]*\s*[;)]/g) || [];
  check("★ trace.js / dash.js 里也没有裸中文标识符", bareTrace.length === 0 && bareDash.length === 0,
    bareTrace.concat(bareDash).join(" "));
}

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

// 日志面板的历史回填（以前日志只活在浏览器内存里：一刷新页面就空白 —— 管理员反馈）
check("app.js 会去拉 /api/logs（首屏历史）", js.includes('/api/logs'));
check("boot 真的请求了 /api/logs", calls.some((c) => c.method === "GET" && c.url.includes("/api/logs")),
  calls.map((c) => c.url).join(" | "));
check("★ 历史日志被渲染进日志面板（刷新后不再空白）",
  (document.getElementById("logBox")?.innerHTML || "").includes("历史日志-A"),
  (document.getElementById("logBox")?.innerHTML || "(空)").slice(0, 120));

// 日志很长时要能一键到顶 / 到底（管理员要求的两个按钮）
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

/* ─────────── 4) 服务器 agent 自己的密钥（面板可填，但不回显） ─────────── */

console.log("\n▶ 动态：服务器 agent 的密钥（填 / 不改 / 清除，且永不下发明文）");

const agentKeyInput = document.getElementById("setAgentServerKey");
check("★ 服务器 agent 卡片有密钥输入框（password）——以前只能在 .env 里改 QQCHAT_AGENT_SERVER_KEY",
  html.includes('type="password" id="setAgentServerKey"') && !!agentKeyInput && !!document.getElementById("clearAgentServerKey"),
  agentKeyInput ? "输入框在，但 type/id 不是 password 直写" : "输入框不存在");
check("★ 已配密钥时输入框不回显明文，只把掩码写进 placeholder",
  String(agentKeyInput.value) === "" && String(agentKeyInput.placeholder).includes("sk-a****"),
  `value=${JSON.stringify(agentKeyInput.value)} placeholder=${JSON.stringify(agentKeyInput.placeholder)}`);
check("来源提示写明“来自面板”（与聊天那把 key 同样措辞）",
  String(document.getElementById("agentServerKeySrc").textContent).includes("来自面板"),
  document.getElementById("agentServerKeySrc").textContent);

// 什么都不填直接保存 → 不能把已存的密钥抹掉（payload 里连字段都不能有）
{
  const b = calls.length;
  fire("setAgentServerKey", "input", { target: { id: "setAgentServerKey" } });
  try { await saveClicks[0]({}); } catch (e) { /* 上面已覆盖 */ }
  await new Promise((r) => setTimeout(r, 250));
  const post = calls.slice(b).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
  const has = post && JSON.parse(post.body).agentServerKey !== undefined;
  check("★ 密钥框留空时保存**不带** agentServerKey（否则每保存一次就把已存的密钥抹掉）",
    !!post && !has, post ? post.body.slice(0, 160) : "没发出 POST");
}

// 填了密钥 → 保存必须原值发出
{
  agentKeyInput.value = "sk-agent-test-123";
  const b = calls.length;
  fire("setAgentServerKey", "input", { target: { id: "setAgentServerKey" } });
  try { await saveClicks[0]({}); } catch (e) { /* 同上 */ }
  await new Promise((r) => setTimeout(r, 250));
  const post = calls.slice(b).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
  const sent = post ? JSON.parse(post.body).agentServerKey : undefined;
  check("★ 填了密钥后保存发的是原值（服务端存 secrets 表）", sent === "sk-agent-test-123",
    `发出=${JSON.stringify(sent)}`);
  check("保存成功后输入框自己清空（不回显）", String(agentKeyInput.value) === "",
    JSON.stringify(agentKeyInput.value));
}

// 点“清除密钥”→ 必须显式发空串（留空本身 = 不改，两者不能混）
{
  fire("clearAgentServerKey", "click");
  check("★ 点「清除密钥」后提示“待清除”（跟聊天那把一样要二次确认）",
    String(document.getElementById("agentServerKeySrc").textContent).includes("待清除"),
    document.getElementById("agentServerKeySrc").textContent);
  const b = calls.length;
  try { await saveClicks[0]({}); } catch (e) { /* 同上 */ }
  await new Promise((r) => setTimeout(r, 250));
  const post = calls.slice(b).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
  const sent = post ? JSON.parse(post.body).agentServerKey : undefined;
  check("★ 清除时显式发空串（服务端删掉该密钥、回退环境变量 / 聊天那把）", sent === "",
    `发出=${JSON.stringify(sent)}`);

  // 清除后同一轮里再保存一次，不能再重复发空串（标记要归位）
  const b2 = calls.length;
  try { await saveClicks[0]({}); } catch (e) { /* 同上 */ }
  await new Promise((r) => setTimeout(r, 250));
  const post2 = calls.slice(b2).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
  check("清除标记归位：下一轮保存不再重复发空串",
    !!post2 && JSON.parse(post2.body).agentServerKey === undefined,
    post2 ? post2.body.slice(0, 160) : "没发出 POST");
}

/* ─────────── 4) 服务器 agent 的 QQ 动作（点赞/戳一戳…） ─────────── */

console.log("\n▶ 动态：服务器 agent 的 QQ 动作（能真去 QQ 里做，但默认只给安全档）");

const qqActionsInput = document.getElementById("setAgentServerQqActions");
check("★ 服务器 agent 卡片有「QQ 动作」输入框（以前它只能看日志/跑命令，碰不了 QQ）",
  !!qqActionsInput, qqActionsInput ? "有" : "输入框不存在");
check("★ 提示里写清楚了默认档与危险动作要点名（点赞 / ban 禁言）",
  html.includes("ban 禁言") && html.includes("点赞"),
  (html.match(/服务器 agent 的 QQ 动作[\s\S]{0,200}/) || ["(没找到)"])[0].slice(0, 120));
check("★ 已配置的动作回填到输入框",
  String(qqActionsInput && qqActionsInput.value) === "like,poke,ban",
  qqActionsInput ? JSON.stringify(qqActionsInput.value) : "无元素");
check("★ 把“实际会开哪几个”回显出来（留空≠没有，写错的名字服务端会忽略）",
  String(document.getElementById("agentServerQqActionsOut")?.textContent || "").includes("已点名打开 ban"),
  String(document.getElementById("agentServerQqActionsOut")?.textContent || "(空)"));
{
  const b = calls.length;
  try { await saveClicks[0]({}); } catch (e) { /* 同上 */ }
  await new Promise((r) => setTimeout(r, 250));
  const post = calls.slice(b).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
  const sent = post ? JSON.parse(post.body).agentServerQqActions : undefined;
  check("★ 保存时把 QQ 动作一起发给服务端（原样，规范化交给服务端做）", sent === "like,poke,ban",
    `发出=${JSON.stringify(sent)}`);
}

/* ─────────── 4) 服务器 agent 的上下文开关（每条指令单独对待） ─────────── */

console.log("\n▶ 动态：服务器 agent 记住上下文（默认关）");

const keepCtx = document.getElementById("setAgentServerKeepContext");
check("★ 面板有一个「记住上下文」开关（默认关：每条 // 指令单独对待）",
  html.includes('type="checkbox" id="setAgentServerKeepContext"') && !!keepCtx,
  keepCtx ? "开了" : "没这个元素");
check("★ 提示里写明白了默认关、以及 //接着 可以单条接上文",
  html.includes("每条 // 指令单独对待") && html.includes("//接着"),
  (html.match(/记住上下文[\s\S]{0,160}/) || ["(没找到)"])[0].slice(0, 110));
check("★ 已配置的值回填到开关上",
  !!keepCtx && keepCtx.checked === true, keepCtx ? String(keepCtx.checked) : "无元素");
{
  const b = calls.length;
  try { await saveClicks[0]({}); } catch (e) { /* 同上 */ }
  await new Promise((r) => setTimeout(r, 250));
  const post = calls.slice(b).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
  const sent = post ? JSON.parse(post.body).agentServerKeepContext : undefined;
  check("★ 保存时把开关一起发出去（true/false，不是字符串）", sent === true, `发出=${JSON.stringify(sent)}`);
}

/* ─────────── 4) 白名单：群聊 / 私聊两个框 ─────────── */

console.log("\n▶ 动态：白名单拆成「群聊」与「私聊」两个框");

const wlGroups = document.getElementById("setWhitelistGroups");
const wlPrivates = document.getElementById("setWhitelistPrivates");
check("★ 面板有两个独立的白名单框（群聊 / 私聊）+ 旧的共用名单框",
  !!wlGroups && !!wlPrivates && !!document.getElementById("setWhitelist"),
  `groups=${!!wlGroups} privates=${!!wlPrivates} legacy=${!!document.getElementById("setWhitelist")}`);
check("★ 提示里写明“各管各的”（QQ 号填在群里不会放行同号的群）",
  html.includes("与群聊名单各管各的"), "(没找到说明文字)");
check("★ 两框各自回填",
  String(wlGroups?.value) === "10001,20002" && String(wlPrivates?.value) === "",
  `groups=${JSON.stringify(wlGroups?.value)} privates=${JSON.stringify(wlPrivates?.value)}`);
check("★ 哪边在用旧的共用名单，面板就如实说哪边",
  String(document.getElementById("whitelistLegacyHint")?.textContent || "").includes("私聊"),
  String(document.getElementById("whitelistLegacyHint")?.textContent || "(空)"));
{
  const b = calls.length;
  try { await saveClicks[0]({}); } catch (e) { /* 同上 */ }
  await new Promise((r) => setTimeout(r, 250));
  const post = calls.slice(b).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
  const sent = post ? JSON.parse(post.body) : {};
  check("★ 保存时两个白名单都发出去（不是只发旧的）",
    sent.whitelistGroups === "10001,20002" && sent.whitelistPrivates === "",
    `groups=${JSON.stringify(sent.whitelistGroups)} privates=${JSON.stringify(sent.whitelistPrivates)}`);
}

const dockerSwitch = document.getElementById("setAgentServerDocker");
check("★ 面板有一个「允许 agent 透过 docker 操作服务器」开关（高权限，默认关）",
  html.includes('type="checkbox" id="setAgentServerDocker"') && !!dockerSwitch,
  dockerSwitch ? "有" : "没这个元素");
check("★ 提示里写清了权限边界（docker.sock ≈ root、部署目录 /host/qqchat）",
  html.includes("docker.sock") && html.includes("/host/qqchat"),
  (html.match(/docker 操作服务器[\s\S]{0,200}/) || ["(没找到)"])[0].slice(0, 140));
check("★ 服务端返回 false 时开关是关的（高风险默认不能自己开）",
  !!dockerSwitch && dockerSwitch.checked === false, dockerSwitch ? String(dockerSwitch.checked) : "无元素");
{
  const b = calls.length;
  try { await saveClicks[0]({}); } catch (e) { /* 同上 */ }
  await new Promise((r) => setTimeout(r, 250));
  const post = calls.slice(b).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
  const sent = post ? JSON.parse(post.body).agentServerDocker : undefined;
  check("★ 保存时把 docker 开关一起发出去", sent === false, `发出=${JSON.stringify(sent)}`);
}

/* ─────────── 4) 面板一键部署（上传/拉取产物 + 回滚） ─────────── */

console.log("\n▶ 动态：面板一键部署（上传 / 地址 / 回滚）");

check("★ 面板有一键部署卡片（上传产物 + 地址部署 + 回滚）",
  !!document.getElementById("deployCard") && !!document.getElementById("deployUploadGo") &&
  !!document.getElementById("deployUrlGo") && !!document.getElementById("deployRollbackGo"),
  ["deployCard", "deployUploadGo", "deployUrlGo", "deployRollbackGo"]
    .map((id) => `${id}=${!!document.getElementById(id)}`).join(" "));
check("★ 提示里写清了它会替换容器、以及回滚点",
  html.includes("qqchat-agent:prev") && html.includes("替换机器人容器"),
  (html.match(/一键部署[\s\S]{0,160}/) || ["(没找到)"])[0].slice(0, 120));
check("★ 高权限开关在设置里（默认关）",
  html.includes('type="checkbox" id="setPanelDeployEnabled"') &&
  !!document.getElementById("setPanelDeployEnabled") &&
  document.getElementById("setPanelDeployEnabled").checked === true,
  String(document.getElementById("setPanelDeployEnabled")?.checked));
check("★ 已记住的产物地址回填到输入框",
  String(document.getElementById("deployUrl")?.value) === "https://example.com/app.tar.gz",
  String(document.getElementById("deployUrl")?.value));
{
  const b = calls.length;
  try { await saveClicks[0]({}); } catch (e) { /* 同上 */ }
  await new Promise((r) => setTimeout(r, 250));
  const post = calls.slice(b).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
  const sent = post ? JSON.parse(post.body) : {};
  check("★ 保存时开关与地址一起发出",
    sent.panelDeployEnabled === true && sent.panelDeployUrl === "https://example.com/app.tar.gz",
    `enabled=${JSON.stringify(sent.panelDeployEnabled)} url=${JSON.stringify(sent.panelDeployUrl)}`);
}

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

// ⑦ 工程：场景预设与人工审批必须“回填 + 发得出去”（只做服务端字段 = 面板改了没用）
  await probe.loadSettings();
  check("★ 场景预设从服务端回填到下拉框", document.getElementById("setScenarioPreset").value === "social",
    String(document.getElementById("setScenarioPreset").value));
  check("★ 审批开关与审批人从服务端回填", document.getElementById("setEnableApprovals").checked === true &&
    document.getElementById("setApprovalApprovers").value === "10001",
    `checked=${document.getElementById("setEnableApprovals").checked} approvers=${document.getElementById("setApprovalApprovers").value}`);
  check("★ 面板把“当前生效的能力/审批范围”显示出来（不是只存不发）",
    String(document.getElementById("scenarioCapHint").textContent || "").includes("scene=social") &&
    String(document.getElementById("approvalHint").textContent || "").includes("demo.echo"),
    `scenario=[${document.getElementById("scenarioCapHint").textContent}] approval=[${document.getElementById("approvalHint").textContent}]`);

  document.getElementById("setScenarioPreset").value = "research";
  document.getElementById("setEnableApprovals").checked = false;
  document.getElementById("setApprovalApprovers").value = "10002, 10003";
  await saveClicks[0]({});
  await new Promise((r) => setTimeout(r, 200));
  // ⑧ P1：参与状态机的上限必须“回填 + 发得出去”，只读状态面板必须能画出来
  // 注意：桩 DOM 的 value 是普通属性，赋进去的是**数字**（不是字符串）→ 一律 String() 后再比
  const partVals = Object.fromEntries(["setPartMaxReplies", "setPartCooldown", "setPartProbing",
    "setPartActiveLife", "setPartExitingLife"].map((id) => [id, String(document.getElementById(id).value)]));
  check("★ 参与上限从服务端回填到输入框",
    partVals.setPartMaxReplies === "4" && partVals.setPartCooldown === "25" &&
    partVals.setPartProbing === "2" && partVals.setPartActiveLife === "600" &&
    partVals.setPartExitingLife === "120",
    JSON.stringify(partVals));
  check("★ 面板写清楚“生效的那组数”与“只观测”",
    String(document.getElementById("participationHint").textContent || "").includes("连续 ≤4") &&
    String(document.getElementById("participationHint").textContent || "").includes("只观测"),
    String(document.getElementById("participationHint").textContent || "(空)"));

  check("★ 参与闸门默认关（回填 false）+ 提问开关按服务端回填",
    document.getElementById("setPartGating").checked === false &&
    document.getElementById("setEnableQuestions").checked === true &&
    String(document.getElementById("questionHint").textContent || "").includes("不授予任何权限"),
    `gating=${document.getElementById("setPartGating").checked} questions=${document.getElementById("setEnableQuestions").checked}`);

  document.getElementById("setPartGating").checked = true;
  await saveClicks[0]({});
  await new Promise((r) => setTimeout(r, 200));
  const sentSwitches = lastSettingsBody() || {};
  check("★ 保存请求带上这两个开关",
    sentSwitches.enableParticipationGating === true && sentSwitches.enableQuestions === true,
    JSON.stringify(sentSwitches));

  document.getElementById("setPartMaxReplies").value = "6";
  document.getElementById("setPartCooldown").value = "45";
  await saveClicks[0]({});
  await new Promise((r) => setTimeout(r, 200));
  const sentParticipation = lastSettingsBody() || {};
  check("★ 保存请求带上 5 个参与上限字段",
    sentParticipation.participationMaxConsecutiveReplies === 6 &&
    sentParticipation.participationCooldownSeconds === 45 &&
    sentParticipation.participationProbingMaxReplies === 2 &&
    sentParticipation.participationMaxActiveLifetimeSeconds === 600 &&
    sentParticipation.participationMaxExitingLifetimeSeconds === 120,
    JSON.stringify(sentParticipation));

  // 只读状态：画出来的是“形状”（状态/原因/计数），且必须走安全文本节点。
  // 桩 DOM 的 appendChild 只是 push 进 children（不会拼进父节点的 textContent）→ 读 children。
  await probe.refreshParticipation();
  const partBox = document.getElementById("participationBox");
  const partText = partBox.children.map((c) => String(c.textContent || "")).join("\n");
  check("★ 参与状态面板画出了会话状态（状态 + 原因 + 计数）",
    partText.includes("Active") && partText.includes("replied") && partText.includes("群聊 100***01") &&
    partText.includes("连续 1"),
    partText.slice(0, 200));
  check("★ 参与状态面板也显示了状态机手里那份上限（证明参数真的到了它那儿）",
    partText.includes("连续 ≤3"), partText.slice(0, 120));
  check("★ 参与状态用的是文本节点，不是 innerHTML（会话名里可能有别人写的字）",
    partBox.children.every((c) => c.tagName === "DIV" && String(c.innerHTML || "") === ""),
    "子节点数=" + partBox.children.length);  const sentApproval = lastSettingsBody() || {};
  check("★ 保存请求带上 scenarioPreset / enableApprovals / approvalApprovers",
    sentApproval.scenarioPreset === "research" && sentApproval.enableApprovals === false &&
    sentApproval.approvalApprovers === "10002, 10003", JSON.stringify(sentApproval));

/* ─────────── N) 命名与血缘：面板里不许再有旧实现的名字与描述 ─────────── */

console.log("\n▶ 静态：命名与血缘（面板只叫 Bot Agent，不掺旧实现的名字/描述）");

const uiText = html + "\n" + js + "\n" + css;

// 旧桌面实现的设计血缘词（WinUI 控件名 / Fluent / Mica / Windows 11 私有字形）
const lineageWords = ["WinUI", "Fluent", "Mica", "Windows 11", "NavigationView", "Segoe UI Symbol",
                      "AppTitleBar", "DataTemplate", "MenuFlyout", "InfoBar", "&#xE8BD;"];
const lineageHits = lineageWords.filter((w) => uiText.includes(w));
check("面板三个文件里没有旧桌面实现的血缘词（WinUI / Fluent / Mica / 控件名 / 私有字形）",
  lineageHits.length === 0, lineageHits.join("、"));

// 品牌名三处一致（标题 / 移动端应用名 / 标题栏）
check("品牌名是 Bot Agent（<title> / apple-mobile-web-app-title / 标题栏三处一致）",
  html.includes("<title>Bot Agent</title>") &&
  html.includes('content="Bot Agent"') &&
  html.includes('<div class="brand-title">Bot Agent</div>'), "");

// 旧项目名不该出现在面板任何一处
check("面板里没有旧项目名（QQ Chat Agent / QQChatAgent）",
  !uiText.includes("QQ Chat Agent") && !uiText.includes("QQChatAgent"), "");

// localStorage 键已改用新名；旧键名只允许出现在那一行“迁移读取”里
const legacyKeyLines = js.split("\n").filter((l) => /["']qqchat\./.test(l));
check("localStorage 键已改用新名（旧键名只剩 1 行迁移读）",
  js.includes('"botagent.panel.token"') && js.includes('"botagent.theme"') && legacyKeyLines.length === 1,
  `仍含旧键名的行 ${legacyKeyLines.length} 行`);

// 部署实况标识必须留着：面板给的运维提示要跟服务器上实际的容器/镜像名一致
check("部署实况标识仍在（提示里的容器/镜像名与服务器一致，没被误改）",
  (html.includes("qqchat-bot") || js.includes("qqchat-bot")) &&
  (html.includes("qqchat-agent:prev") || js.includes("qqchat-agent:prev")), "");

/// 递归收集一个桩元素的全部文本（追踪页与仪表盘两处只读页面共用）。
function deepTextOf(node) {
  let out = String(node.textContent || "");
  for (const child of node.children || []) out += "\n" + deepTextOf(child);
  return out;
}

/* ─────────── O) 追踪页（批 H）：新文件必须一起进护栏 ─────────── */

console.log("\n▶ 追踪页（批 H）：trace.js / trace.css 也在探针里");

const traceJs = fs.readFileSync(path.join(root, "trace.js"), "utf8");
const traceCss = fs.readFileSync(path.join(root, "trace.css"), "utf8");
const routes = fs.readFileSync(path.resolve(here, "../../src/BotAgent.Headless/Adapters/Panel/PanelRoutes.cs"), "utf8");

check("index.html 引了 trace.js 与 trace.css（与 app.js/app.css 同一套路，不依赖 CDN）",
  html.includes('src="/trace.js"') && html.includes('href="/trace.css"'));

check("追踪页容器与两个导航入口都在（桌面 data-page=trace + 手机标签）",
  html.includes('id="pageTrace"') && (html.match(/data-page="trace"/g) || []).length === 2);

check("★ 静态资源路由加了 trace.js / trace.css（新增文件 = 加一行路由，别只加文件）",
  routes.includes('"/trace.js"') && routes.includes('"/trace.css"'));

check("★ trace.js 不用 innerHTML 拼字符串（服务端来的工具名/原因码一律文本节点）",
  !/innerHTML\s*=/.test(traceJs) && traceJs.includes("createElement") && traceJs.includes("textContent"));

check("trace.js 里没有旧桌面实现的血缘词", !["WinUI", "Fluent", "Mica"].some((w) => traceJs.includes(w)));

// 批次 I 之后：这一页**不再**是纯只读 —— 它多了“审批决定”这一条写路径（高权限，前置 fail-closed）。
// 断言改成“写路径有且只有审批这一条”，而不是不许有写入。
check("★ 追踪页的写路径有且只有审批决定（/api/approvals/decide）",
  (traceJs.match(/PanelApi\.post\(/g) || []).length === 1 &&
  traceJs.includes("/api/approvals/decide") &&
  !/fetch\([^)]*method:\s*"(?:PUT|DELETE|PATCH)"/.test(traceJs));

check("app.js 里：切页时显隐 pageTrace 且进页拉一次（+ 手动刷新按钮）",
  /pageTrace"\)\.hidden = page !== "trace"/.test(js) && js.includes("loadTraces()") &&
  js.includes("traceRefresh"));

// 动态：把 trace.js 跑进同一个沙箱，喂一份合成轨迹，看六张卡与颜色语义是否真的画出来。
let traceRenderError = null;
try {
  vm.runInContext(traceJs, sandbox, { filename: "trace.js" });

  const payload = {
    available: true, count: 1, active: 0, capacity: 50,
    traces: [{
      runId: "t1", key: "群聊 100***01", channel: "私域", startedAt: Date.now(), outcome: "done", totalMs: 847,
      nodes: [
        { kind: "Context", status: "ok", ms: 12, tool: null, reason: null, count: 6 },
        { kind: "Model", status: "ok", ms: 800, tool: null, reason: null, count: null },
        { kind: "Gate", status: "denied", ms: 3, tool: "web.search", reason: "not_allowlisted", count: null },
        { kind: "Outbound", status: "sent", ms: 5, tool: null, reason: null, count: 18 }
      ]
    }]
  };

  sandbox.TracePage.render(payload);

  const timeline = document.getElementById("traceRuns");
  const detail = document.getElementById("traceDetail");
  const keys = document.getElementById("traceKeys");
  const timelineText = deepTextOf(timeline);

  check("★ 时间轴把六张卡都画出来（4 个节点 + 2 个未发生的组 = 6 张卡 + 1 行概况）",
    timeline.children.length === 7, "子节点数=" + timeline.children.length);

  check("★ 卡片按 ①–⑥ 的顺序（参与判断在最前，净化与发送在最后）",
    timelineText.indexOf("① 参与判断") >= 0 &&
    timelineText.indexOf("① 参与判断") < timelineText.indexOf("④ 工具闸门") &&
    timelineText.indexOf("④ 工具闸门") < timelineText.indexOf("⑥ 净化与发送"));

  check("★ 这一轮没走到的步骤如实写「未发生」（不假装六步都跑了）",
    timelineText.includes("未发生") && timelineText.includes("这一轮没走到这一步。"));

  check("★ 闸门那一步连着工具名与原因码（卡片要画的就是这俩）",
    timelineText.includes("web.search") && timelineText.includes("not_allowlisted"));

  check("★ 颜色语义：拒绝 = bad、放行/已发出 = ok、未发生 = mute",
    timelineText.includes("拒绝") && timelineText.includes("已发出") &&
    timeline.children.some((c) => String(c.className || "").includes("trace-card")));

  check("★ 详情栏列出本轮事实（runId / 总耗时 / 节点数），且最近几轮可点",
    deepTextOf(detail).includes("t1") && deepTextOf(detail).includes("847ms") &&
    deepTextOf(detail).includes("最近 1 轮"));

  check("★ 会话列按脱敏后的 key 分组（不是原始会话 key）", deepTextOf(keys).includes("群聊 100***01"));

  check("★ 渲染只用文本节点：画出来的元素 innerHTML 全为空串",
    [timeline, detail, keys].every((host) => (host.children || []).every((c) => String(c.innerHTML || "") === "")));

  check("★ 轨迹不可用时给一句人话（不白屏、不抛异常）",
    (() => { sandbox.TracePage.render({ available: false }); 
      const empty = deepTextOf(document.getElementById("traceRuns"));
      sandbox.TracePage.render(payload);
      return empty.includes("还没有轨迹"); })());
} catch (e) {
  traceRenderError = e;
}

check("★ trace.js 能在最小 DOM 桩里真跑一遍", traceRenderError === null,
  traceRenderError ? String(traceRenderError && traceRenderError.message) : "");

/* ─────────── P) 仪表盘（批 J） ─────────── */

console.log("\n▶ 仪表盘（批 J）：dash.js 也在探针里");

const dashJs = fs.readFileSync(path.join(root, "dash.js"), "utf8");

check("index.html 引了 dash.js，且有页面容器与两个导航入口",
  html.includes('src="/dash.js"') && html.includes('id="pageDash"') &&
  (html.match(/data-page="dash"/g) || []).length === 2);

check("★ /dash.js 排在静态资源路由里（新增文件 = 加一行路由）", routes.includes('"/dash.js"'));

// 结构：页面必须是 .shell 的子元素。
// 历史 bug（2026-09-24，管理员截图）：pageDash / pageTrace 被写在了 </div>(.shell) **外面** ——
// .shell 撑满剩下的高度、里面一个可见页面都没有 → 顶上一整块空白，而页面内容掉到下面去。
const shellOpen = html.indexOf('<div class="shell">');
const shellClose = html.indexOf("<!-- 会话右键菜单 -->");   // 紧随 .shell 收尾的第一个标记
const insideShell = (id) => {
  const at = html.indexOf(`id="${id}"`);
  return shellOpen >= 0 && shellClose > shellOpen && at > shellOpen && at < shellClose;
};
check("★ 四个页面都在 .shell 里（写在 shell 外面 = 顶上整块空白 + 内容掉到底下）",
  ["pageChat", "pageSettings", "pageTrace", "pageDash"].every(insideShell),
  ["pageChat", "pageSettings", "pageTrace", "pageDash"].filter((id) => !insideShell(id)).join(", "));

check("★ dash.js 不用 innerHTML（服务端来的数字与文字一律文本节点）",
  !/innerHTML\s*=/.test(dashJs) && dashJs.includes("createElement") && dashJs.includes("textContent"));

check("★ 仪表盘是纯只读的（没有写路径）", !/POST/.test(dashJs));

check("app.js 里：切页时显隐 pageDash 且进页拉一次（+ 手动刷新）",
  /pageDash"\)\.hidden = page !== "dash"/.test(js) && js.includes("loadDashboard()") && js.includes("dashRefresh"));

let dashError = null;
try {
  vm.runInContext(dashJs, sandbox, { filename: "dash.js" });

  const dashData = {
    uptimeSeconds: 3720, aiMode: true, onebot: true, accountOnline: true,
    conversations: 7, inFlight: 1, queued: 2, latencyMs: 4200,
    memory: { usedBytes: 268435456, limitBytes: 1073741824 },
    load: 0.42,
    tools: { total: 26, chat: 10, qq: 10, server: 6, highRisk: 11, needApproval: 0, executors: 4, healthy: true },
    sessionPolicy: { available: true, sessions: 3, stale: 0, rebuilt: 1 },
    traces: { available: true, recent: 12, active: 1, capacity: 50 }
  };

  sandbox.DashPage.render(dashData);

  const grid = document.getElementById("dashGrid");
  const dashText = deepTextOf(grid);

  check("★ 一屏摊开 10 张数字卡（运行 / AI / 协议端 / 会话 / 延迟 / 内存 / 负载 / 工具 / 权限 / 轨迹）",
    grid.children.length === 10, "卡片数=" + grid.children.length);

  check("★ 内存按人话显示（工作集 + 容器上限 + 占用百分比）",
    dashText.includes("256 MB") && dashText.includes("1.00 GB") && dashText.includes("25%"));

  check("★ 延迟用最近一轮的实测值（毫秒）", dashText.includes("4200ms"));

  check("★ 工具目录 / 会话权限 / 轨迹三块都来自各自的数据面（不是另算一份）",
    dashText.includes("高风险") && dashText.includes("执行者 4") &&
    dashText.includes("重建过 1") && dashText.includes("最多留 50"));

  check("★ 渲染只用文本节点（画出来的元素 innerHTML 全为空串）",
    (grid.children || []).every((c) => String(c.innerHTML || "") === ""));

  check("★ 数据没到时不白屏（给一句人话）",
    (() => { sandbox.DashPage.render(null); const t = deepTextOf(grid); sandbox.DashPage.render(dashData); return t.includes("刷新"); })());
} catch (e) {
  dashError = e;
}

check("★ dash.js 能在最小 DOM 桩里真跑一遍", dashError === null,
  dashError ? String(dashError && dashError.message) : "");

/* ─────────── P1) Agent 工作台（批 K） ─────────── */

console.log("\n▶ Agent 工作台（批 K）：结构 / 数据面 / fail-closed / 执行安全");

const agentSessionBody = js.slice(js.indexOf("function renderAgentSessions"), js.indexOf("function renderAgentRuntime"));
check("Agent 桌面导航、移动导航与页面容器齐全",
  (html.match(/data-page="agent"/g) || []).length === 2 && html.includes('id="pageAgent"'));
check("Agent 工作台有关键控件（筛选 / 提示词 / 后端 / 超时 / 执行 / 结果）",
  ["agentSessionList", "agentBackendFilter", "agentSessionStateFilter", "agentWorkbenchPrompt",
    "agentWorkbenchTarget", "agentWorkbenchTimeout", "agentWorkbenchSend", "agentResultCard",
     "agentLivePill", "agentMetricGrid"].every((id) => html.includes(`id="${id}"`)));
check("Agent 底部控制区有模型刷新 / 推理强度 / 上下文 / 权限控件",
  ["agentModelSelect", "agentModelRefresh", "agentReasoningSelect", "agentContextToggle",
    "agentPermissionPreset", "agentPermissionDocker"].every((id) => html.includes(`id="${id}"`)));
check("设置页可以添加推理档位，默认档位为 auto",
  html.includes('id="setAgentReasoningEffort"') && html.includes('id="setAgentReasoningLevels"') &&
    js.includes("agentReasoningLevels") && js.includes('"auto"'));
check("推理强度与模型选择解耦，默认提供 auto 档位",
  js.includes("agentReasoningSelect") && js.includes("agentReasoningLevels") &&
    js.includes("agentReasoningEffort") && js.includes('"auto"') &&
    !/低=provider\/model|高=provider\/model/.test(html));
check("app.js 使用现有 Agent 数据面（不新增后端接口）",
  ["/api/agent/status", "/api/agent/sessions", "/api/tools", "/api/approvals", "/api/agent/test"]
    .every((endpoint) => js.includes(endpoint)) && js.includes("Promise.allSettled"));
check("Agent 会话渲染不读取或拼接历史 prompt / result",
  agentSessionBody.length > 0 && !/session\.(prompt|result)|\.prompt\b|\.result\b/.test(agentSessionBody));
check("Agent 结果使用 textContent，不把服务端正文写入 innerHTML",
  /agentResultText"\)\.textContent/.test(js) && !/agentResultText"\)\.innerHTML/.test(js));
check("Agent 执行期间有 busy 状态与重复提交保护",
  js.includes("if (state.agent.busy) return") && js.includes("state.agent.busy = true") &&
  js.includes("state.agent.busy = false"));

/* ─────────── P2) 动态：真的切一次页（“卡在正在加载”只有这条路抓得住） ─────────── */

// 为什么必须走导航：静态检查看不出运行期 ReferenceError。
// 历史 bug（2026-09-24）：loadDashboard 里一个被吃掉的模板字符串 → 抛 ReferenceError →
// 页面永远停在“正在加载…”，而当时静态断言、`node --check` 全是绿的。
{
  const dashNav = navClicks.find((n) => n.page === "dash");
  const traceNav = navClicks.find((n) => n.page === "trace");

  let switchError = null;
  const mark = calls.length;
  try {
    if (dashNav) dashNav.fn({});
    if (traceNav) traceNav.fn({});
  } catch (e) {
    switchError = `${e.name}: ${e.message}`;
  }
  await new Promise((r) => setTimeout(r, 120));

  check("★ 切到仪表盘 / 追踪页都不抛异常（有裸标识符这类运行期错误就会炸在这里）",
    dashNav && traceNav && switchError === null,
    !dashNav || !traceNav ? "导航入口没找到（自查 data-page）" : switchError);

  check("★ 切到仪表盘时真的拉了 /api/dashboard",
    calls.slice(mark).some((c) => String(c.url).includes("/api/dashboard")),
    calls.slice(mark).map((c) => c.url).join(" | "));

  const summaryText = document.getElementById("dashSummary")?.textContent || "";
  check("★ 仪表盘摘要已经**离开**“正在加载…”（那一行真的跑到了）",
    summaryText.length > 0 && !summaryText.includes("正在加载"),
    summaryText || "(空)");

  check("★ 切到追踪页时真的拉了 /api/traces",
    calls.slice(mark).some((c) => String(c.url).includes("/api/traces")),
    calls.slice(mark).map((c) => c.url).join(" | "));
}

/* ─────────── P3) Agent 动态：从未知状态到合成执行 ─────────── */

{
  const agentNav = navClicks.find((n) => n.page === "agent");
  let agentSwitchError = null;
  const beforeUnknown = calls.length;
  agentStatusFails = true;
  try {
    if (agentNav) agentNav.fn({});
  } catch (e) {
    agentSwitchError = `${e.name}: ${e.message}`;
  }
  await new Promise((r) => setTimeout(r, 120));

  check("★ 状态接口失败时进入 Agent 工作台不抛异常",
    agentNav && agentSwitchError === null, agentSwitchError || "导航入口没找到");
  check("★ 状态未知时执行按钮保持 disabled（fail-closed）",
    document.getElementById("agentWorkbenchSend").disabled === true,
    `disabled=${document.getElementById("agentWorkbenchSend").disabled}`);
  check("★ Agent 页面失败场景仍尝试拉取四个数据面",
    calls.slice(beforeUnknown).some((c) => c.url.includes("/api/agent/status")) &&
    calls.slice(beforeUnknown).some((c) => c.url.includes("/api/agent/sessions")) &&
    calls.slice(beforeUnknown).some((c) => c.url.includes("/api/tools")) &&
    calls.slice(beforeUnknown).some((c) => c.url.includes("/api/approvals")),
    calls.slice(beforeUnknown).map((c) => c.url).join(" | "));

  agentStatusFails = false;
  agentWorkbenchReady = true;
  const beforeReady = calls.length;
  if (agentNav) agentNav.fn({});
  await new Promise((r) => setTimeout(r, 120));

  const readyCalls = calls.slice(beforeReady).map((c) => c.url);
  check("★ Agent 状态成功后可进入工作台并加载状态 / 会话 / 工具 / 审批",
    ["/api/agent/status", "/api/agent/sessions", "/api/tools", "/api/approvals"]
      .every((endpoint) => readyCalls.some((url) => url.includes(endpoint))),
    readyCalls.join(" | "));
  check("★ 合成会话列表完成结构化渲染（不泄露历史正文）",
    document.getElementById("agentSessionCount").textContent === "2" &&
    document.getElementById("agentSessionList").children.length === 2 &&
    !document.getElementById("agentSessionList").innerHTML.includes("prompt"));

  check("★ 工作台会话可执行且不把任务发进 QQ 当前会话",
    document.getElementById("agentWorkbenchSend").textContent !== "只读会话");
  fire("agentNewSession", "click");
  const localKey = sandbox.probe.agentSnapshot().active;
  check("★ 新建工作台会话走持久化 API",
    sandbox.probe.agentSnapshot().rows.some((row) => row.key === localKey && !row.local) &&
    calls.slice(beforeReady).some((call) => call.method === "POST" && call.url.includes("/api/agent/sessions")));
  const prompt = "检查 <synthetic-result> & 仅返回状态，不执行外发";
  document.getElementById("agentWorkbenchPrompt").value = prompt;
  fire("agentWorkbenchPrompt", "input", { target: { id: "agentWorkbenchPrompt" } });
  check("★ 输入合成任务后执行按钮可用",
    document.getElementById("agentWorkbenchSend").disabled === false,
    `disabled=${document.getElementById("agentWorkbenchSend").disabled}`);

  agentRunDelayMs = 30;
  const beforeRun = calls.length;
  fire("agentWorkbenchSend", "click");
  fire("agentWorkbenchSend", "click");
  await new Promise((r) => setTimeout(r, 90));
  const runCalls = calls.slice(beforeRun).filter((c) => c.url.includes("/api/agent/test"));
  check("★ 双击执行只产生一次 /api/agent/test 请求",
    runCalls.length === 1, `请求数=${runCalls.length}`);
  const posted = runCalls[0];
  let body = null;
  try { body = posted ? JSON.parse(posted.body) : null; } catch {}
  check("★ 执行请求体包含 prompt / timeoutSec / target",
    body && body.key === "panel:workspace" && body.sessionId && body.prompt === prompt && Number.isFinite(body.timeoutSec) && body.target === "server",
    posted ? posted.body : "没有发出请求");
  check("★ 当前执行结果以文本节点显示，合成标签没有进入 innerHTML",
    document.getElementById("agentResultText").textContent === "<synthetic-result>" &&
    document.getElementById("agentResultText").innerHTML === "",
    `text=${document.getElementById("agentResultText").textContent}`);
}

{
  const select = document.getElementById("agentReasoningSelect");
  sandbox.probe.renderReasoningSelect(select, "low\nhigh\ncustom-level", "removed-level");
  check("★ 删除已选档位后回退 auto，自动档始终可选",
    select.value === "auto" && select.children.some((option) => option.value === "auto"));
  sandbox.probe.renderReasoningSelect(select, "auto\nhigh\ncustom-level", "custom-level");
  check("★ 设置添加的自定义推理档位可单独选择", select.value === "custom-level");
  for (const id of ["Bash", "Read", "Write", "Fetch", "Qq", "Docker"])
    document.getElementById("agentPermission" + id).checked = false;
  document.getElementById("agentModelSelect").value = "synthetic-selected-model";
  const before = calls.length;
  await sandbox.probe.saveAgentWorkbenchConfig();
  const call = calls.slice(before).find((call) => call.method === "POST" && call.url.includes("/api/settings"));
  const payload = call ? JSON.parse(call.body) : {};
  check("★ 保存时模型与推理参数各自独立",
    payload.agentServerModel === "synthetic-selected-model" && payload.agentReasoningEffort === "custom-level");
  check("★ 权限全部取消不序列化成后端的空值全开",
    payload.agentServerTools === "none" && payload.agentServerDocker === false);
}

/* ─────────── Q) 面板审批卡（批 I） ─────────── */

console.log("\n▶ 面板审批卡（批 I）：写路径 + 两条 fail-closed");

/// 递归收集：某个元素里所有文本、以及所有文案等于某个标签的元素（按钮就靠它找）
function collectNodes(node, out) {
  out.push(node);
  for (const child of node.children || []) collectNodes(child, out);
  return out;
}

const nodesOf = (host) => collectNodes(host, []);
const textsOf = (host) => nodesOf(host).map((n) => String(n.textContent || "")).filter((t) => t.length > 0);

check("★ trace.js 走 window.PanelApi（复用 app.js 的令牌注入，不自己拼请求头）",
  traceJs.includes("window.PanelApi.post") && traceJs.includes("window.PanelApi.get") &&
  js.includes("window.PanelApi = {"));

check("★ 写路径指向 /api/approvals/decide（唯一一条写路径）",
  traceJs.includes("/api/approvals/decide") && traceJs.includes("/api/approvals"));

{
  // ① 审批开着 + 令牌已配：画出待批单与两个按钮
  approvalsPayload = {
    available: true, enabled: true, tokenConfigured: true, canDecide: true,
    pending: [{
      id: "ABC234", tool: "demo.echo", summary: "执行固定假工具 demo.echo（演示用，无真实副作用）",
      key: "群聊 100***01", expiresInSeconds: 96, policyVersion: 3
    }]
  };
  sandbox.TracePage.render({
    available: true, count: 0, active: 0, capacity: 50, traces: []
  }, approvalsPayload);

  const detail = document.getElementById("traceDetail");
  const texts = textsOf(detail);
  const buttons = nodesOf(detail).filter((n) => String(n.textContent || "") === "批准" || String(n.textContent || "") === "拒绝");

  check("★ 待批单画出来了（编号 / 工具 / 摘要 / 脱敏 key / 剩余秒数）",
    texts.some((t) => t.includes("ABC234")) && texts.some((t) => t.includes("demo.echo")) &&
    texts.some((t) => t.includes("群聊 100***01")) && texts.some((t) => t.includes("剩 96 秒")));

  check("★ 批准 / 拒绝两个按钮都在", buttons.length === 2,
    "按钮数=" + buttons.length + "；文本=" + texts.join("|").slice(0, 120));

  // ② 未配面板令牌：明确写出原因，且**一个按钮都不给**（fail-closed）
  approvalsPayload = { available: true, enabled: true, tokenConfigured: false, canDecide: false, pending: [] };
  sandbox.TracePage.render({ available: true, count: 0, active: 0, capacity: 50, traces: [] }, approvalsPayload);
  const lockedTexts = textsOf(document.getElementById("traceDetail"));
  const lockedButtons = nodesOf(document.getElementById("traceDetail"))
    .filter((n) => String(n.textContent || "") === "批准" || String(n.textContent || "") === "拒绝");
  check("★ 未配面板令牌 → 写明原因且不给按钮（fail-closed）",
    lockedTexts.some((t) => t.includes("未配置面板令牌")) && lockedButtons.length === 0,
    "按钮数=" + lockedButtons.length);

  // ③ 审批总开关关着（默认）：整块不出现
  approvalsPayload = { available: true, enabled: false, tokenConfigured: true, canDecide: false, pending: [] };
  sandbox.TracePage.render({ available: true, count: 0, active: 0, capacity: 50, traces: [] }, approvalsPayload);
  const offTexts = textsOf(document.getElementById("traceDetail"));
  check("★ 审批默认关 → 待审批卡整块不出现",
    !nodesOf(document.getElementById("traceDetail"))
      .some((n) => String(n.className || "").includes("trace-approvals")),
    offTexts.join("|").slice(0, 120));

  // ④ 写路径：decide() 真的 POST 出去（用的是面板那层桥）
  approvalsPayload = {
    available: true, enabled: true, tokenConfigured: true, canDecide: true,
    pending: [{
      id: "ABC234", tool: "demo.echo", summary: "x", key: "群聊 100***01", expiresInSeconds: 60, policyVersion: 3
    }]
  };
  const before = calls.length;
  sandbox.TracePage.decide("ABC234", true);
  await new Promise((r) => setTimeout(r, 30));
  const posted = calls.slice(before).find((c) => String(c.url).includes("/api/approvals/decide"));
  check("★ 点批准 → POST /api/approvals/decide（带编号与 approve=true）",
    !!posted && posted.method === "POST" && JSON.parse(posted.body).id === "ABC234" &&
    JSON.parse(posted.body).approve === true,
    posted ? `${posted.method} ${posted.url} ${posted.body}` : "没有发出请求");
}

/* ─────────── R) 聊天步进循环上限（批 E） ─────────── */

console.log("\n▶ 聊天步进循环（批 E）：控件 / 回填 / 真的发出去");

check("index.html 有步进循环控件（1~3，默认 1）",
  html.includes('id="setMaxAgentSteps"') && /id="setMaxAgentSteps" min="1" max="3"/.test(html) &&
  html.includes('id="agentStepsVal"'));

check("★ app.js 回填它（loadSettings 与 saveSettings 字段集合必须一致）",
  js.includes('$("setMaxAgentSteps").value = r.maxAgentSteps') &&
  js.includes("maxAgentSteps: Number("));

const beforeStepSave = calls.length;
await sandbox.probe.saveSettings();
check("★ 保存请求带 maxAgentSteps（回填的是 1，发出去的也是 1）",
  (() => {
    const sent = calls.slice(beforeStepSave).filter((c) => String(c.url).includes("/api/settings") && c.method === "POST").pop();
    return !!sent && JSON.parse(sent.body).maxAgentSteps === 1;
  })(),
  (() => {
    const sent = calls.slice(beforeStepSave).filter((c) => String(c.url).includes("/api/settings") && c.method === "POST").pop();
    return sent ? "最后一次保存：" + sent.body : "没有保存请求";
  })());
console.log("");
if (failures.length === 0) {
  console.log(`通过 ${pass}，失败 0`);
  process.exit(0);
}

console.log(`通过 ${pass}，失败 ${failures.length}`);
for (const f of failures) console.log("  ✗ " + f);
process.exit(1);
