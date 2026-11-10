#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

artifacts=.tmp/pipeline-probe-artifacts
evidence="$artifacts/evidence/$(date -u '+%Y%m%dT%H%M%SZ')"
mkdir -p "$evidence"
{
  date -u '+%Y-%m-%dT%H:%M:%SZ'
  git rev-parse HEAD
  git branch --show-current
  dotnet --version
} > "$evidence/environment.txt"

dotnet build tests/BotAgent.ReplyPipelinePacingProbe/BotAgent.ReplyPipelinePacingProbe.csproj \
  -c Release -t:Rebuild --artifacts-path "$artifacts" --nologo -v:quiet 2>&1 | tee "$evidence/build.txt"

bin="$artifacts/bin/BotAgent.ReplyPipelinePacingProbe/release"
sha256sum "$bin/BotAgent.ReplyPipelinePacingProbe.dll" "$bin/BotAgent.Engine.dll" \
  "$bin/BotAgent.Core.dll" > "$evidence/candidate.sha256"

for run in 1 2 3; do
  echo "REPEAT $run"
  dotnet "$bin/BotAgent.ReplyPipelinePacingProbe.dll" "$@" 2>&1 | tee "$evidence/run-$run.txt"
done

sha256sum --check "$evidence/candidate.sha256"
echo "EVIDENCE $evidence"
