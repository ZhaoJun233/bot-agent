# Reverse transport probe

Synthetic loopback-only acceptance for handshake deadlines, bounded pending clients, active-client continuity/replacement, shutdown, HTTP completions, and forward/reverse frame handling. [Program](<Program.cs>) drives the lifecycle checks; [ReviewTransportRegression](<ReviewTransportRegression.cs>) retains the protocol checks. [The project](<BotAgent.ReverseTransportProbe.csproj>) source-links actual transports and references actual Core. It does not load application conversations or use production services.

## Run

From the repository root, force rebuilding because source-writing tools may preserve timestamps:

```bash
dotnet build tests/BotAgent.ReverseTransportProbe/BotAgent.ReverseTransportProbe.csproj -c Release -t:Rebuild --artifacts-path .tmp/reverse-transport-check
dotnet .tmp/reverse-transport-check/bin/BotAgent.ReverseTransportProbe/release/BotAgent.ReverseTransportProbe.dll
```

Expected: **44 PASS checks**, `failures=0`, exit 0. The real handshake deadline is exercised; this is not an instantaneous unit test.

## Closure-race diagnosis — 2026-11-06

A frozen full-suite run passed its first five checks, including successful closure of both pending clients, then failed at the concurrent drip writer:

```text
System.InvalidOperationException: The operation is not allowed on non-connected sockets.
Program.cs:49 (repeated partial.GetStream)
Program.cs:76 (await drip)
```

Ranked hypotheses: late stream acquisition after observed disconnection; failure of the transport deadline; premature disposal disrupting the active client. The first predicts the exact exception even after closure succeeds, and predicts that retaining the connected stream restores the existing I/O exception contract. The original five PASS results oppose the second hypothesis; unchanged valid-client/replacement checks cover the third.

**Finding: fixture contract misuse, not a product change.** `TcpClient.GetStream()` requires a currently connected socket even when a stream was obtained earlier. The reader and drip writer share a client; after an observed socket error updates its connected state, a later acquisition throws before `WriteAsync` reaches the existing `IOException` handling.

Important limit: graceful EOF alone is not guaranteed to change the connected flag. A retained negative experiment returned `read=0, Connected=True`; it did not reproduce this guard. A separate controlled abortive peer close yielded `read=IOException, Connected=False`, followed by the exact original `GetStream()` exception. The same replay binary, changing only acquisition to the retained stream, passed **five fresh runs** with the expected closed-write `IOException`. This demonstrates the race mechanism without inventing which remote close variant occurred in the original run. Five unchanged full-suite repetitions also passed; that does not disprove the intermittent original failure.

## Narrow repair and preserved checks

Acquire each relevant `NetworkStream` once immediately after connecting, before any reader/writer observes closure. Both the drip writer and `Closed(NetworkStream, TimeSpan)` use that retained stream. The single-use pending-flood writer still acquires once per new client.

- All **14 original `Check` call-sites** remain equivalent, allowing only the stream-argument substitution; the normal path executes 13 of those checks before the protocol regression checks.
- Every original time budget, drip count predicate, connected-state assertion, and exception filter is unchanged. No timeouts, assertions or filters were broadened.
- A separate finite probe copied the final closure helper verbatim: an open peer times out to **false**, EOF/reset return **true**, and local disposal still propagates `ObjectDisposedException`. Unexpected local misuse is not counted as successful remote closure.
- Only the lifecycle fixture code changed. The protocol regression source, project file, product source, and the separately frozen reply-pipeline fixture were not edited in this task.

## Final verification and evidence

Forced build: **0 warnings / 0 errors**. **20 fresh full-probe processes** each passed all **44 checks**, `failures=0`: **880 assertion executions**. Per-run native output was inspected, and the runner rechecked unchanged candidate/source SHA-256 values. All jobs were collected before freeze.

Local ignored evidence is deliberately listed as code, not Markdown links, so clean-checkout documentation validation does not depend on private build artifacts:

```text
Original native failure: .tmp/final-verification-5/ReverseTransportProbe.log
Original binary/source hashes: .tmp/reverse-final-diagnosis/original.sha256
Original fixture copy: .tmp/reverse-final-diagnosis/original-Program.cs
Unchanged repeats: .tmp/reverse-final-diagnosis/original-{1..5}.log
Graceful-EOF negative experiment: .tmp/reverse-final-diagnosis/contract-red.log
Controlled acquisition failure: .tmp/reverse-final-diagnosis/contract-reset-red.log
Controlled retained-stream passes: .tmp/reverse-final-diagnosis/contract-reset-green-{1..5}.log
Controlled replay: .tmp/reverse-final-diagnosis/run-contract-reset.sh
Closure semantic controls: .tmp/reverse-final-diagnosis/semantics.log
Closure helper copy: .tmp/reverse-final-diagnosis/closure-semantics/Program.cs
Forced build: .tmp/reverse-final-diagnosis/green-build.log
Full native repeats: .tmp/reverse-final-diagnosis/green-{1..20}.log
Per-run counts and hashes: .tmp/reverse-final-diagnosis/verification-summary.json
Repeat runner: .tmp/reverse-final-diagnosis/run-green.sh
Scoped diff: .tmp/reverse-final-diagnosis/fixture.diff
Candidate manifests: .tmp/reverse-final-diagnosis/green-{binaries,sources}.sha256
```

Frozen fingerprints:

```text
Original probe: ba56ed490d0d75818d70c806ba084a087a2ff4664c80836c618f484c73cb8c39
Final probe:    1ba91d52c5c4430d5b5b21fc48f9bbbb2fbf7b958643768a7a99fff201950358
Final Core:     5ff43b3611f57a5160788fb89d5d7d56083475414fa3f22ac33079862d54a39d
Program.cs:     82ce8181dac4df37ec9802fd5ba04ff279004754d17d462607807da55da13a84
Product source unchanged:
f4376baa2e07765c5a5528b4559d298ce3dfcef198120972fc6011c7b28b57e7  src/BotAgent.Platforms/OneBot/ReverseWsTransport.cs
```

This is local synthetic validation. No production access, commit, push or deployment occurred.
