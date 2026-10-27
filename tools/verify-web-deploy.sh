#!/usr/bin/env bash
# 面板前端部署后验收（在服务器上跑）：
#   ① index.html 里每个以 / 开头的 src/href 都必须在 /web/ 下，且对应文件真实存在；
#   ② 经 Caddy（带闸门 Cookie）逐个请求，必须 200 且 Content-Type 对得上。
# 只看 /web/ 返回 200 不够——2026-10-27 那次白屏 /web/ 也是 200，挂的是 JS/CSS。
# Cookie 从 Caddyfile 现读，不打印、不落盘。
# 用法：sudo bash verify-web-deploy.sh [web目录] [Host]
set -euo pipefail
WEB_DIR="${1:-/var/www/botagent-web}"
# 站点域名必须显式传入（仓库里不写真实域名），例：verify-web-deploy.sh /var/www/botagent-web panel.example.com
HOST="${2:?用法: verify-web-deploy.sh <web目录> <站点域名>}"
CADDYFILE="${CADDYFILE:-/etc/caddy/Caddyfile}"
BASE="http://127.0.0.1:8081"
fail=0

refs=$(grep -oE '(src|href)="/[^"]*"' "$WEB_DIR/index.html" | sed -E 's/^(src|href)="([^"]*)"$/\2/')
[ -n "$refs" ] || { echo "✗ index.html 里没有任何资源引用"; exit 1; }

cookie=$(grep -m1 -oE '@authed header Cookie [^ ]+' "$CADDYFILE" | awk '{print $4}' | tr -d '*"' || true)

check() { # path expected-type-regex
  local p="$1" want="$2" out code ctype
  out=$(curl -s -o /dev/null -w '%{http_code} %{content_type}' -H "Host: $HOST" ${cookie:+-H "Cookie: $cookie"} "$BASE$p")
  code=${out%% *}; ctype=${out#* }
  if [ "$code" = 200 ] && [[ "$ctype" =~ $want ]]; then echo "  ok $p ($code $ctype)"
  else echo "  ✗ $p ($code $ctype)"; fail=1; fi
}

echo "① 资源引用与文件"
for r in $refs; do
  case "$r" in
    /web/*) f="$WEB_DIR/${r#/web/}"
            if [ -f "$f" ]; then echo "  ok $r"; else echo "  ✗ $r 文件不存在"; fail=1; fi ;;
    *) echo "  ✗ $r 不在 /web/ 下（构建 base 错了，会白屏）"; fail=1 ;;
  esac
done

echo "② 经 Caddy 请求"
if [ -z "$cookie" ]; then echo "  ! 没读到闸门 Cookie，跳过 HTTP 检查"; fail=1
else
  check /web/ 'text/html'
  for r in $refs; do
    case "$r" in
      *.js) check "$r" 'javascript' ;;
      *.css) check "$r" 'text/css' ;;
      /web/*) check "$r" '.' ;;
    esac
  done
fi

[ $fail = 0 ] && echo "✓ 面板前端验收通过" || { echo "✗ 面板前端验收失败"; exit 1; }
