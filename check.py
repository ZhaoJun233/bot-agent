#!/usr/bin/env python3
"""
check.py — BotAgent.Headless 面板代码静态检查
用法:
    python3 check.py            # 检查测试线
    python3 check.py --main     # 检查主仓库线
    python3 check.py --diff     # 仅对比两线差异
    python3 check.py --fix-hint # 输出修复建议（不自动改文件）
"""

import re, sys, os, subprocess
from pathlib import Path

# ── 路径配置 ──────────────────────────────────────────────────────────────────
ROOT      = Path(__file__).parent
WWWROOT   = ROOT / "src/BotAgent.Headless/wwwroot"
MAIN_ROOT = ROOT / "Main/bot-agent"
MAIN_WWW  = MAIN_ROOT / "src/BotAgent.Headless/wwwroot"

FILES = ["app.css", "app.js", "index.html", "dash.js", "trace.js", "trace.css"]

# ── 颜色输出 ──────────────────────────────────────────────────────────────────
RED   = "\033[91m"
GRN   = "\033[92m"
YLW   = "\033[93m"
BLD   = "\033[1m"
RST   = "\033[0m"

def ok(msg):   print(f"  {GRN}✔{RST}  {msg}")
def err(msg):  print(f"  {RED}✘{RST}  {BLD}{msg}{RST}")
def warn(msg): print(f"  {YLW}⚠{RST}  {msg}")
def hdr(msg):  print(f"\n{BLD}{'─'*60}{RST}\n{BLD}{msg}{RST}")

errors = 0
warnings = 0

def fail(msg):
    global errors
    errors += 1
    err(msg)

def caution(msg):
    global warnings
    warnings += 1
    warn(msg)

# ══════════════════════════════════════════════════════════════════════════════
# 1. 文件存在性
# ══════════════════════════════════════════════════════════════════════════════
def check_files_exist(www: Path):
    hdr("1. 文件存在性")
    for f in FILES:
        p = www / f
        if p.exists():
            ok(f"{f}  ({p.stat().st_size:,} bytes)")
        else:
            fail(f"{f} 不存在！")

# ══════════════════════════════════════════════════════════════════════════════
# 2. CSS 花括号平衡
# ══════════════════════════════════════════════════════════════════════════════
def check_css_braces(www: Path):
    hdr("2. CSS 花括号平衡")
    for fname in [f for f in FILES if f.endswith(".css")]:
        p = www / fname
        if not p.exists():
            continue
        text = p.read_text(encoding="utf-8")
        # 去掉注释与字符串再计数，避免误判
        stripped = re.sub(r'/\*.*?\*/', '', text, flags=re.DOTALL)
        # 去掉 content: '...' / "..." 里的括号
        stripped = re.sub(r'content\s*:\s*(["\']).*?\1', 'content:""', stripped, flags=re.DOTALL)
        opens  = stripped.count('{')
        closes = stripped.count('}')
        if opens == closes:
            ok(f"{fname}  opens={opens}  closes={closes}  ✔ 平衡")
        else:
            fail(f"{fname}  opens={opens}  closes={closes}  差值={opens-closes}  【括号不平衡！】")

# ══════════════════════════════════════════════════════════════════════════════
# 3. 所有页面 ID 在 CSS 中均有对应背景/滚动规则
# ══════════════════════════════════════════════════════════════════════════════
PAGE_IDS = ["pageChat", "pageAgent", "pageSettings", "pageDash", "pageTrace"]

def check_page_css_coverage(www: Path):
    hdr("3. 页面 ID → CSS 覆盖检查")
    css_text = ""
    for f in FILES:
        if f.endswith(".css") and (www / f).exists():
            css_text += (www / f).read_text(encoding="utf-8")

    # index.html 里实际存在的页面 ID
    html_path = www / "index.html"
    actual_ids = []
    if html_path.exists():
        actual_ids = re.findall(r'id="(page\w+)"', html_path.read_text())

    # 提取所有含 background 的 #pageXxx 选择器组
    bg_rule = re.findall(r'(#page\w+(?:\s*,\s*#page\w+)*)\s*\{[^}]*background[^}]*\}', css_text)
    covered = set()
    for rule in bg_rule:
        for pid in re.findall(r'#(page\w+)', rule):
            covered.add(pid)

    # 只对「有独立背景需求」的页面报警（Chat/Agent 纯靠 .page 基类，属于正常设计）
    NEED_BG = {"pageDash", "pageTrace", "pageSettings"}
    for pid in actual_ids:
        if pid in NEED_BG:
            if pid in covered:
                ok(f"#{pid}  有 background 规则")
            else:
                fail(f"#{pid}  缺少 background 规则（该页面需要显式背景声明）")
        else:
            ok(f"#{pid}  继承 .page 基类背景（正常）")

# ══════════════════════════════════════════════════════════════════════════════
# 4. JS 语法快速检查（node --check，无需运行）
# ══════════════════════════════════════════════════════════════════════════════
def check_js_syntax(www: Path):
    hdr("4. JS 语法检查（node --check）")
    node = subprocess.run(["which", "node"], capture_output=True, text=True)
    if node.returncode != 0:
        caution("未找到 node，跳过 JS 语法检查（建议安装 nodejs）")
        return
    for fname in [f for f in FILES if f.endswith(".js")]:
        p = www / fname
        if not p.exists():
            continue
        r = subprocess.run(["node", "--check", str(p)], capture_output=True, text=True)
        if r.returncode == 0:
            ok(f"{fname}  语法正常")
        else:
            fail(f"{fname}  语法错误:\n{r.stderr.strip()}")

def check_frontend_probe(root: Path):
    hdr("4b. 前端探针冒烟测试（probe.mjs）")
    probe_script = root / "tests/BotAgent.FrontendProbe/probe.mjs"
    if not probe_script.exists():
        caution("未找到 probe.mjs，跳过")
        return
    r = subprocess.run(["node", str(probe_script)], capture_output=True, text=True)
    if r.returncode == 0:
        ok("probe.mjs 全部契约与冒烟测试通过")
    else:
        fail(f"probe.mjs 测试失败:\n{r.stdout.strip()}\n{r.stderr.strip()}")

# ══════════════════════════════════════════════════════════════════════════════
# 5. 已知低级 Bug 模式扫描
# ══════════════════════════════════════════════════════════════════════════════
BUG_PATTERNS = [
    # (文件名, 正则, 描述, 严重级别 'err'|'warn')

    # ── 高危 ──────────────────────────────────────────────────────────────────
    ("app.js", r'injectCardSaveButtons',
     "残留函数 injectCardSaveButtons（已废弃，会导致 JS 运行时错误）", "err"),

    ("app.js", r'headerSaveBtn\s*\.\s*style\s*\.\s*display\s*=\s*["\']none["\']',
     "headerSaveBtn.style.display='none' 在非设置页调用时会崩溃（元素不存在）", "err"),

    # ── 中危 ──────────────────────────────────────────────────────────────────
    # #pageDash 单独成一条规则（行首就是 #pageDash {），说明没有与 #pageTrace 合并
    ("app.css", r'(?m)^#pageDash\s*\{',
     "#pageDash 单独声明规则，#pageTrace 可能缺少同等覆盖（建议合并为 #pageDash, #pageTrace {...}）", "warn"),

    # innerHTML 里有疑似外部数据插值且没过 escapeHtml（排除字面量数字/静态字符串）
    ("app.js",  r'\.innerHTML\s*=\s*`[^`]*\$\{(?!escapeHtml\()(?!["\'\`\d])[^}]{4,}\}[^`]*`',
     "innerHTML 模板字符串中疑似有未经 escapeHtml 处理的外部数据插值（XSS 风险）", "warn"),
]

def check_bug_patterns(www: Path):
    hdr("5. 已知 Bug 模式扫描")
    any_hit = False
    for fname, pattern, desc, level in BUG_PATTERNS:
        p = www / fname
        if not p.exists():
            continue
        text = p.read_text(encoding="utf-8")
        hits = [(i+1, line.strip()) for i, line in enumerate(text.splitlines())
                if re.search(pattern, line)]
        if hits:
            any_hit = True
            if level == "err":
                fail(f"[{fname}] {desc}")
            else:
                caution(f"[{fname}] {desc}")
            for lineno, line in hits[:3]:
                print(f"       L{lineno}: {line[:120]}")
    if not any_hit:
        ok("未发现已知 Bug 模式")

# ══════════════════════════════════════════════════════════════════════════════
# 6. 插件卡片防溢出检查（历史高频 bug）
# ══════════════════════════════════════════════════════════════════════════════
def check_plugin_card_overflow(www: Path):
    hdr("6. 插件卡片防溢出样式检查")
    p = www / "app.js"
    if not p.exists():
        return
    text = p.read_text(encoding="utf-8")

    # 找到插件卡片渲染块（catBadge 附近）
    m = re.search(r'catBadge.*?statusBadge.*?</div>\s*`', text, re.DOTALL)
    if not m:
        caution("未找到插件卡片渲染块，跳过")
        return
    block = m.group(0)

    checks = [
        ("min-width:0",           "外层 flex 容器缺 min-width:0（子元素会撑破布局）"),
        ("text-overflow:ellipsis","名称/ID 缺 text-overflow:ellipsis（长名称溢出）"),
        ("overflow:hidden",       "名称/ID 缺 overflow:hidden"),
        ("white-space:nowrap",    "名称/ID 缺 white-space:nowrap"),
        ("flex-shrink:0",         "statusBadge 包裹层缺 flex-shrink:0（标签会被挤压）"),
        ("overflow-wrap:anywhere","描述缺 overflow-wrap:anywhere（长 URL 不换行）"),
    ]
    for token, desc in checks:
        if token in block:
            ok(f"{token}")
        else:
            caution(f"{desc}")


def check_grid2_responsive(www: Path):
    hdr("6b. .grid-2 移动端响应式回退检查")
    css_text = ""
    for f in FILES:
        if f.endswith(".css") and (www / f).exists():
            css_text += (www / f).read_text(encoding="utf-8")

    if ".grid-2" not in css_text:
        caution(".grid-2 类未定义，跳过")
        return

    # 在 @media 块内找 .grid-2 { grid-template-columns: 1fr }
    media_blocks = re.findall(r'@media[^{]*\{(.*?)\n\}', css_text, re.DOTALL)
    found_fallback = any(
        re.search(r'\.grid-2\s*\{[^}]*grid-template-columns\s*:\s*1fr\s*;', blk)
        for blk in media_blocks
    )
    if found_fallback:
        ok(".grid-2 在媒体查询中有 1fr 单列回退")
    else:
        caution(".grid-2 在媒体查询中未找到 1fr 单列回退，移动端可能双列溢出")

# ══════════════════════════════════════════════════════════════════════════════
# 7. DLL 新鲜度检查（避免跑旧产物）
# ══════════════════════════════════════════════════════════════════════════════
def check_dll_freshness(www: Path):
    hdr("7. DLL 新鲜度（静态文件 vs 产物时间戳）")
    dll = ROOT / "src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll"
    if not dll.exists():
        caution("bin/ 下 DLL 不存在，尚未编译")
        return

    dll_mtime = dll.stat().st_mtime
    stale = []
    for fname in FILES:
        fp = www / fname
        if fp.exists() and fp.stat().st_mtime > dll_mtime:
            stale.append(fname)

    import datetime
    dll_time = datetime.datetime.fromtimestamp(dll_mtime).strftime("%m-%d %H:%M:%S")
    if stale:
        fail(f"DLL 时间戳 {dll_time} 早于以下静态文件，【必须重新编译再重启】:")
        for f in stale:
            fp = www / f
            ft = datetime.datetime.fromtimestamp(fp.stat().st_mtime).strftime("%m-%d %H:%M:%S")
            print(f"       {f}  ({ft})")
        print(f"\n       {YLW}修复命令:{RST}")
        print(f"       cd {ROOT}")
        print(f"       bash stop.sh && dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release && bash start.sh")
    else:
        ok(f"DLL ({dll_time}) 是最新的，所有静态文件已嵌入")

# ══════════════════════════════════════════════════════════════════════════════
# 8. 与主仓库 diff 摘要
# ══════════════════════════════════════════════════════════════════════════════
def check_diff_summary(www: Path, main_www: Path):
    hdr("8. 与主仓库差异摘要")
    if not main_www.exists():
        caution(f"主仓库路径不存在: {main_www}，跳过 diff")
        return
    for fname in FILES:
        a = main_www / fname
        b = www / fname
        if not a.exists() or not b.exists():
            caution(f"{fname}: 一方不存在，无法 diff")
            continue
        r = subprocess.run(
            ["diff", "-u", "--label", f"main/{fname}", "--label", f"test/{fname}", str(a), str(b)],
            capture_output=True, text=True
        )
        if r.returncode == 0:
            ok(f"{fname}  与主仓库完全一致")
        else:
            lines = r.stdout.splitlines()
            added   = sum(1 for l in lines if l.startswith('+') and not l.startswith('+++'))
            removed = sum(1 for l in lines if l.startswith('-') and not l.startswith('---'))
            caution(f"{fname}  +{added} / -{removed} 行差异（相对主仓库）")

# ══════════════════════════════════════════════════════════════════════════════
# 主入口
# ══════════════════════════════════════════════════════════════════════════════
def main():
    args = sys.argv[1:]
    use_main = "--main" in args
    diff_only = "--diff" in args

    www = MAIN_WWW if use_main else WWWROOT
    label = "主仓库线" if use_main else "测试线"

    print(f"\n{BLD}{'═'*60}{RST}")
    print(f"{BLD}  BotAgent 面板代码检查  [{label}]{RST}")
    print(f"{BLD}  目录: {www}{RST}")
    print(f"{BLD}{'═'*60}{RST}")

    if diff_only:
        check_diff_summary(WWWROOT, MAIN_WWW)
    else:
        check_files_exist(www)
        check_css_braces(www)
        check_page_css_coverage(www)
        check_js_syntax(www)
        check_frontend_probe(ROOT)
        check_bug_patterns(www)
        check_plugin_card_overflow(www)
        check_grid2_responsive(www)
        check_dll_freshness(www)
        if not use_main:
            check_diff_summary(www, MAIN_WWW)

    print(f"\n{BLD}{'═'*60}{RST}")
    if errors == 0 and warnings == 0:
        print(f"{GRN}{BLD}  全部通过，无任何问题。{RST}")
    else:
        if errors:
            print(f"{RED}{BLD}  错误: {errors} 项  （必须修复，否则面板运行异常）{RST}")
        if warnings:
            print(f"{YLW}{BLD}  警告: {warnings} 项  （建议修复）{RST}")
    print(f"{BLD}{'═'*60}{RST}\n")

    sys.exit(1 if errors else 0)

if __name__ == "__main__":
    main()
