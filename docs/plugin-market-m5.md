# M5 Python source packages

Python is an additive `ApiVersion: 2` package family; .NET remains strict `ApiVersion: 1`. Python descriptors contain common identity/version/hash/download/minimum-host/permissions/dependencies/settings fields and `Runtime` only. Managed entry fields are rejected even when null. Runtime kind is `python`, protocol is `1`, dependency mode is `stdlib-only`, and minimum Python is `3.11` through `3.99`. The entry is a safe relative ASCII `.py` path (maximum 240 characters), not the reserved root SDK `botagent_worker.py`. Tools are 1–64 unique, case-sensitive names matching `[A-Za-z0-9_-]{1,64}`.

## Feeds and artifacts

The separate grouped `python-repository.json` has `{SchemaVersion:2,Plugins:[{Id,Versions:[manifest]}]}`. Optional `python-presentation.json` reuses presentation metadata with runtime `python`. Legacy `index.json`, `repository.json`, and managed artifact paths never accept Python. External GitHub import discovers both strict feeds at one immutable source revision. Same-ID cross-runtime entries and cross-runtime dependencies are rejected. Preview binds descriptor, feed, governance, and installed-state revision; confirmation rechecks them.

ZIP packages contain the declared entry and canonical `botagent_worker.py`. Existing compressed/expanded/entry/count/duplicate/traversal/symlink bounds still apply. Python additionally allows only UTF-8 source and passive data, no dependency-install hooks, environments, bytecode, native libraries, or executable payloads. No pip is invoked. Review package code before granting trust: these shape checks are not a security sandbox.

## Readiness and lifecycle

Set an absolute trusted interpreter path in `BOTAGENT_PYTHON_EXECUTABLE` (legacy `QQCHAT_PYTHON_EXECUTABLE` fallback). Catalog and installed responses expose `isCompatible` and `compatibilityMessage`. Readiness runs only the trusted interpreter `--version`; it does not import or execute the package. Missing/old interpreter or unsupported dependencies blocks preview/confirmation. Successful readiness means interpreter version compatibility only; initialization/discovery runs at explicit enable.

Install reuses durable package state and defaults disabled. Disabled packages spawn no worker. Explicit enable creates a shadow runtime directory, resolves settings, clears inherited environment, starts isolated Python bootstrap, verifies process ownership before entering package code, then performs initialize/discover. Discovery failure never publishes a Ready source. Update/rollback retain the existing package transaction and drain old runtime; disable/uninstall detach source and terminate the worker. Restart uses the stored enabled intent. Package failure does not execute a failed tool call again.

## Agent permission and generation boundary

Local Python tools share ToolInventory and McpToolUseCase; they are `FileOrShell`, not remote-MCP authority. Names are `mcp.python_<24-hex-package-id-hash>.<tool>`. The broker adds a fresh random owner token on each attachment and runtime generation captured under the worker gate. Execution validates the exact approved lease against the captured source and passes the admitted generation into the worker serialized gate. Detach/re-enable, replacement, restart, and failure invalidate previous admission.

Existing panel task admission requires the exact named tool and existing task-level approval; it captures immutable leases before approval and forwards them into Agent execution. Ordinary `//` requests lacking such admission fail closed for local tools. There is no parallel per-tool approval mechanism and no automatic authority from discovery/read-only hints. Existing platform source leases, access scope, argument validation, budgets, result limits, audit redaction, and cancellation continue to apply.

## Process boundary limitations

Windows uses a private Job object with no breakaway and kill-on-close; ordinary descendants die on worker exit/disposal/host exit. Unix verifies a new session/process group and terminates ordinary descendants; explicitly detached sessions can escape. Unix ownership was not natively exercised in this Windows development environment. This is lifecycle ownership, not an OS sandbox. Startup ownership failure fails closed before package code. Never run untrusted code expecting filesystem/network containment.

Native synthetic probes: `BotAgent.PythonWorkerProbe` (worker/protocol), `BotAgent.PythonCleanupProbe` (owned descendants), `BotAgent.PythonAgentProbe` (actual Agent admission/execution), and `BotAgent.PythonMarketProbe` (shared descriptor cases and public preview/install/update/rollback/uninstall, disabled/enable/disable source lifecycle). No production chat data or remote deployment is used.

Run the market probe from this checkout with `dotnet run --project tests/BotAgent.PythonMarketProbe -c Release -- /absolute/path/to/python`. Its committed synthetic `Fixtures/python-contract.json` and `Fixtures/worker.py` originate from the matching M5 producer shared vectors/example; the probe creates a deterministic temporary ZIP using the host SDK source, computes its hash, and routes all HTTP through a local fake. An optional three-argument mode accepts a shared fixture JSON, external producer artifact directory, and absolute interpreter path for cross-repository parity. No downloaded dependency or network service is needed. The unmodified architecture probe must pass without raising any thresholds.
