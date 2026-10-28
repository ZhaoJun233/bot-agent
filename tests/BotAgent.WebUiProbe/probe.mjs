/**
 * 面板前端静态检查（无浏览器、无进程）
 *
 * 为什么需要它：
 *   新面板的前端由后端 GET /api/settings/schema 下发的分组动态渲染，
 *   DOM 元素全靠字符串 id 约定。app.js 引用了 index.html 里不存在的 id 时，
 *   没有任何编译期报错，浏览器里只表现为「某个面板打不开」或「点了没反应」——
 *   等到用户点上去才发现，排查成本远高于一条静态断言。
 *
 * 检查项：
 *   1. app.js 语法通过
 *   2. app.js 引用的每个 DOM id 都在 index.html 中存在
 *   3. 鉴权链路函数齐全（登录卡片、会话探测、本地痕迹清理、401 兜底）
 *   4. 登录请求体键名与后端读取的键名一致
 *   5. app.js 调用的每个 /api/* 路径在 WebUiPlugin.cs 中都有对应字面量
 *   6. 面板配置键（非密钥）都能在 AppSettings 找到同名属性
 *   7. 免鉴权白名单包含登录端点与健康探针
 *
 * 运行：node tests/BotAgent.WebUiProbe/probe.mjs
 * 退出码：0 = 全部通过，1 = 有失败项
 *
 * 边界：这是静态文本检查，不启动进程、不发 HTTP。它挡得住「改了 HTML 忘了改 JS」
 * 这类机械错配，挡不住逻辑错误；运行时行为仍需人工验证。
 */
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../..");
const webroot = path.join(root, "src/BotAgent.Plugins.WebUi/wwwroot");
const appJsPath = path.join(webroot, "app.js");
const htmlPath = path.join(webroot, "index.html");
const pluginCsPath = path.join(root, "src/BotAgent.Plugins.WebUi/WebUiPlugin.cs");
const settingsCsPath = path.join(root, "src/BotAgent.Headless/Services/AppSettings.cs");

const results = [];

function check(name, ok, detail = "") {
  results.push({ name, ok: !!ok, detail });
}

function readIfExists(p, label) {
  if (!fs.existsSync(p)) {
    check(`${label} 存在`, false, `未找到 ${p}`);
    return null;
  }
  return fs.readFileSync(p, "utf8");
}

const appJs = readIfExists(appJsPath, "app.js");
const html = readIfExists(htmlPath, "index.html");
const pluginCs = readIfExists(pluginCsPath, "WebUiPlugin.cs");
const settingsCs = readIfExists(settingsCsPath, "AppSettings.cs");

// ---------- 1. 语法 ----------
if (appJs) {
  const syntax = spawnSync(process.execPath, ["--check", appJsPath], { encoding: "utf8" });
  check("app.js 语法通过", syntax.status === 0, (syntax.stderr || "").trim());
}

// ---------- 2. DOM id 一致性 ----------
if (appJs && html) {
  const ids = new Set([...html.matchAll(/id="([^"]+)"/g)].map(m => m[1]));
  // 只认 getElementById("字面量") 形式；"pane-" + tab 这类拼接无法静态求值，
  // 不纳入断言，但仍统计出来让读者知道存在动态构造。
  const refs = [...appJs.matchAll(/getElementById\(\s*["`]([^"`]+)["`]\s*\)/g)].map(m => m[1]);
  const dynamic = [...appJs.matchAll(/getElementById\(\s*["`]([^"`]+)["`]\s*\+/g)].map(m => m[1]);
  const missing = [...new Set(refs)].filter(r => !ids.has(r));
  check(
    "app.js 引用的 DOM id 都存在于 index.html",
    missing.length === 0,
    missing.length
      ? `缺失: ${missing.join(", ")}`
      : `静态引用 ${new Set(refs).size} 个，动态前缀 ${dynamic.length} 个${dynamic.length ? `（${[...new Set(dynamic)].join(", ")}）` : ""}`
  );
}

// ---------- 3. 鉴权链路函数 ----------
if (appJs) {
  const fns = [
    "ensureAuthenticated",
    "fetchAuthStatus",
    "clearLocalAuthTraces",
    "showAuthCard",
    "hideAuthCard",
    "bindAuthForm",
    "afterLogin"
  ];
  const absent = fns.filter(f => !new RegExp(`function\\s+${f}\\s*\\(`).test(appJs));
  check(
    "鉴权链路函数齐全",
    absent.length === 0,
    absent.length ? `缺失: ${absent.join(", ")}` : fns.join(", ")
  );
}

// ---------- 4. 登录请求体键名 ----------
if (appJs && pluginCs) {
  const frontSendsPw = /\{\s*pw\s*:/.test(appJs);
  const backReadsPw = /node\?\["pw"\]/.test(pluginCs);
  check(
    "登录请求体键名前后端一致（pw）",
    frontSendsPw && backReadsPw,
    `前端发送=${frontSendsPw}，后端读取=${backReadsPw}`
  );
}

// ---------- 5. API 路径对齐 ----------
if (appJs && pluginCs) {
  const endpoints = [...appJs.matchAll(/apiFetch\(\s*[`"]([^`"]+)[`"]/g)].map(m => m[1]);
  const expected = [
    ...new Set(
      endpoints.map(e => "/api" + e.split("?")[0].replace(/\$\{[^}]*\}/g, ""))
    )
  ];
  const missing = expected.filter(p => !pluginCs.includes(`"${p}"`));
  check(
    "app.js 的 API 路径都在 WebUiPlugin 中存在",
    missing.length === 0,
    missing.length ? `缺失: ${missing.join(", ")}` : `共 ${expected.length} 条`
  );
}

// ---------- 6. 配置键与 AppSettings 对齐 ----------
if (pluginCs && settingsCs) {
  const sections = pluginCs
    .split('key = "')
    .slice(1)
    .map(seg => {
      const end = seg.indexOf('"');
      const name = end > 0 ? seg.slice(0, end) : "";
      // 一个字段的声明长度有限，取 400 字符足以覆盖 type 属性
      const body = seg.slice(0, 400);
      return { name, secret: /type = "secret"/.test(body) };
    })
    .filter(s => /^[A-Za-z0-9_]+$/.test(s.name));

  const props = new Set(
    [...settingsCs.matchAll(/public\s+[\w<>?\[\]\.]+\s+(\w+)\s*\{\s*get;\s*set;/g)].map(m => m[1])
  );

  const plain = sections.filter(s => !s.secret);
  const secrets = sections.filter(s => s.secret);
  const missingProps = plain.filter(s => !props.has(s.name)).map(s => s.name);

  check(
    "面板配置键（非密钥）都在 AppSettings 中存在",
    missingProps.length === 0,
    missingProps.length ? `缺失: ${missingProps.join(", ")}` : `${plain.length} 个键全部命中`
  );
  check(
    "密钥类配置标为 secret（不写入 settings.json）",
    secrets.length > 0,
    secrets.map(s => s.name).join(", ")
  );
}

// ---------- 7. 免鉴权白名单 ----------
if (pluginCs) {
  const need = ['"/api/auth/"', '"/healthz"', '"/readyz"'];
  const absent = need.filter(s => !pluginCs.includes(s));
  check(
    "免鉴权白名单含登录端点与健康探针",
    absent.length === 0,
    absent.length ? `缺失: ${absent.join(", ")}` : need.join(", ")
  );
}

// ---------- 输出 ----------
for (const r of results) {
  console.log(`${r.ok ? "[OK]  " : "[FAIL]"} ${r.name}${r.detail ? " — " + r.detail : ""}`);
}

const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} 项通过`);
if (failed.length) {
  console.log("失败项:");
  for (const r of failed) {
    console.log(`  - ${r.name}${r.detail ? ": " + r.detail : ""}`);
  }
  process.exit(1);
}