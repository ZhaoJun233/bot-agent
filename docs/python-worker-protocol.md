# Python worker protocol v1

Status: local M5 implementation. No deployment, publication, or independent acceptance is implied. See [the task-scoped Agent slice](python-agent-slice.md) for the real approval/lease path and native test scope.

## Boundary and authority

Python is a separate process using the existing MCP tool-source interface, not a second tool registry. The host must bind it to the same inventory, source lease, permission classification, named-tool approval and execution gate. A discovered read-only hint is metadata, never an authority grant. Disabled packages must not be discovered: source construction performs no filesystem/process I/O, while discovery explicitly starts package code.

**A process is not a security sandbox.** Approved package code runs with the host account's filesystem and network privileges. Context capabilities are declarations and the SDK's require_capability helper is a cooperative check, not OS enforcement. This slice provides no host filesystem/network broker, event subscription, pip operation, automatic dependency installation, interpreter installation or cloud service. Windows Job Object ownership or POSIX process-group ownership is established before package code starts; ordinary descendants remain owned after the worker exits. Unsupported ownership fails closed. Deliberate detached/breakaway descendants are not a security containment guarantee.

Windows cleanup has an existing native verification record. POSIX cleanup also ran natively on Ubuntu 24.04.5 with Python 3.12 for public commit `c335ff79719264fcee125efbf42a4eb48c00b3f4`: `PythonCleanupProbe` passed 24 checks using Unix process groups in [CI run 42383728264](https://github.com/ZhaoJun233/bot-agent/actions/runs/42383728264), job 128439482996. This covers ordinary owned descendants, disposal/crash/timeout cleanup and preservation of an unrelated synthetic control process. It does not prove interpreter availability, Worker operation or cleanup inside the target Linux Docker image; validate those before enabling Python there. Keep platform/candidate records separate from package review and deployment acceptance.

The operator chooses a trusted interpreter executable (an absolute path is recommended). Readiness invokes only that executable with --version, never a package entry/import. Required version is exactly 3.<minor>, minor 11..99; compatible means Python major 3 and version at least that minimum. Unavailable or incompatible interpreters fail closed. Do not silently try unrelated executables or install one.

## Host source API

- PythonWorkerOptions(Id, ExecutablePath, PackageDirectory, EntryPoint, AllowedTools, CallTimeout, MaxResultBytes), with Configuration JsonObject, GrantedCapabilities, RequiredPythonVersion (default 3.11), MaxStartsPerMinute (default 3, hard range 1..10).
- PythonWorkerToolSource(options, optional shape-only log) implements IMcpToolSource: DiscoverAsync, CallAsync, DisposeAsync, Enabled and State. LastErrorCode is a safe short health code. Configuration is copied and redacted by ToString.
- PythonWorkerInterpreter.CheckAsync(executablePath, requiredVersion, cancellationToken) returns Available, Compatible, Version and ReasonCode; no package execution.
- Qualified names remain mcp.<Id>.<tool>. Tool ServerId is <Id>_g<Generation>; this is part of the lease fingerprint. Each snapshot also carries IsLocalProcess=true and SourceGeneration as an invariant decimal string, captured under the discovery gate. The broker must prepend its owner UUID to that captured string, not reread the source counter after discovery returns. Generation increments on start, fault and stop. GenerationChanged is a nonblocking observation callback, not a reentrant lifecycle hook. A host replacing the entire source must also replace its owner/lease identity: a new object's counter starts again.
- CallWithGenerationAsync(name, arguments, expectedGeneration, cancellationToken) checks the admitted process generation atomically under the same gate as dispatch; mismatch returns mcp_source_changed with a known non-executed outcome. The host broker must use this method for leased calls in addition to its per-instance owner identity.
- Discover once for activation, attach only the ready source; detach before disposal. Discover returns only explicitly allowed tools. Calls recheck the allowlist and known discovery snapshot. Calls never start or replay a process. After failure a fresh discovery may restart within the rolling 60-second budget. There is no autonomous retry loop.

An empty source allowlist is disabled and causes no I/O. Initial Python package manifests require nonempty, unique, case-sensitive tool names matching [A-Za-z0-9_-]{1,64}, at most 64 tools. Entry paths are package-relative ASCII slash-separated .py paths, at most 240 characters; absolute paths, colon/backslash, empty/dot/dotdot components and symlink/junction ancestry are rejected before execution. Host package checks additionally require the SDK and archive validation.

## Package SDK

The canonical SDK is [botagent_worker.py](../src/BotAgent.Mcp/PythonWorkerSdk/botagent_worker.py). Package it byte-for-byte as botagent_worker.py at the archive root; bind its hash and the worker entry into the immutable archive checksum. Do not keep an independently copied SDK in the author repository. The package bootstrap uses Python -I -X utf8 -u, explicitly adding only the package root to imports. The child environment is cleared except trusted SystemRoot/WINDIR/TEMP/TMP necessities; host credentials and PYTHONPATH are not inherited. Stdlib-only describes supported dependency provisioning, not an OS sandbox.

Use [the echo example](../examples/PythonWorker/worker.py). Decorators:

- @plugin.tool(name=..., description=..., input_schema={"type":"object",...}, read_only=False): function receives (arguments, context); return a string or JSON-serializable value. Async functions are also supported, with a separate loop for each invocation.
- @plugin.on_load and @plugin.on_unload: functions receive context. They run at initialize and graceful shutdown. A wedged hook is killed on the host deadline.
- context.configuration and context.capabilities contain only what the host explicitly provided. Context repr hides values. require_capability(name) raises if absent but cannot constrain arbitrary Python code.
- plugin.run() owns stdin/stdout. Hook/tool print output is redirected to stderr. Module-level writes before run or direct writes to stdout violate the framing contract and fault the source. Avoid writing secrets to diagnostics even though the host discards their content.

## NDJSON wire

UTF-8, one compact JSON object plus LF per frame. Requests use integer ids, JSON-RPC 2.0, method and object params. Responses echo the id and contain exactly one of result/error. There is one outstanding request per worker; parallel host callers queue behind a cancellation-aware semaphore.

1. initialize params: protocolVersion:1, configuration:{...}, capabilities:[...]. Result protocolVersion:1. on_load must finish before tools are published.
2. tools/list params:{}; result tools:[{name,description,inputSchema,annotations:{readOnlyHint}}]. Unknown tools are not authorized. Invalid/duplicate metadata fails the source closed.
3. tools/call params:{name,arguments:{...}}; result {content:string,isError:boolean}. Non-string SDK values become JSON text. SDK handler exceptions become generic tool errors, never raw exception messages/tracebacks. Worker-supplied error content is discarded.
4. shutdown params:{}; result:{} after on_unload, then worker exits.

Unsupported version, invalid frame/id/shape, EOF or oversized frames fault the entire channel. No worker-to-host RPC, ambient callback capabilities or unsolicited notifications are supported in v1.

## Bounds and failures

- 512 KiB protocol frame/request ceiling; bounded streaming reads, no unbounded ReadLine allocation.
- 64 discovered tools, name 64 characters, description 400 characters, input schema 16 KiB; object schemas only.
- Arguments 32 KiB before write; configured result ceiling 1..256 KiB. A complete oversized result is refused and cannot be replayed.
- Call/start timeout: configured positive duration <=120 seconds, otherwise 20 seconds. Queue wait shares the deadline.
- Stderr is continuously drained in 4096-byte blocks and counted but never retained or logged. More than 64 KiB over one process lifetime terminates that worker and its ordinary descendants. Logs/audits contain ids, fixed codes, counts, bytes and durations only.
- Caller cancellation throws OperationCanceledException; local timeout returns mcp_timeout. Host shutdown returns python_disposed for an interrupted call. A request that may have reached the worker has UnknownOutcome=true after timeout, protocol loss, crash or RPC error. No implicit retry occurs.
- Normal disposal allows shutdown/on_unload 2 seconds, then kills the process tree; kill/reap wait is bounded to 2 seconds and stderr completion to 1 second. Active calls are cancelled first and do not block unload indefinitely.
- One failed worker does not fault other sources. State/LastErrorCode expose the failure, no raw local paths/arguments/configuration are included.

## Synthetic verification

Run from the repository root:

~~~sh
dotnet run --project tests/BotAgent.PythonWorkerProbe/BotAgent.PythonWorkerProbe.csproj -c Release -- /absolute/path/to/python
~~~

The optional first argument or BOTAGENT_TEST_PYTHON selects an explicit interpreter; otherwise the probe uses python. BOTAGENT_TEST_TEMP can choose a test scratch directory; the default is the probe's build output (not an arbitrary user TEMP). The probe uses only synthetic local files/processes and cleans them up; it never opens bot stores or connects to real chat/model services.

Coverage includes actual round trips/hooks/settings, interpreter incompatibility, no implicit startup/authorization, environment stripping, redacted errors/logs, concurrent calls, argument/result/frame caps, malformed metadata/version/stdout, process crash/cancel/reap, active drain, load/unload timeouts, restart budget/generation, no replay after a synthetic side effect, and per-plugin failure isolation. Host packaging/governance and ToolInventory lease integration are separate host tests, not claimed by this runtime-only probe.
