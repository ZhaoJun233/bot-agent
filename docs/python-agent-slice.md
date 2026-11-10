# Python task-scoped Agent slice

## Authority path

The panel's existing explicit task path now uses the same task approval, model loop,
ToolGate, schema validation, budget and MCP audit path as the rest of the Agent:

1. Host activation/readiness populates the worker catalogue. Admission does not launch code.
2. TaskApprovalGate.Prepare pins the effective task scope and captures read-only
   McpToolInfo snapshots for **explicitly named**, exact-case local tools.
3. AgentTaskRequest carries PythonToolLeases. An absent legacy field defaults to empty;
   an inherited/empty scope does not implicitly grant Python authority.
4. Existing AgentTaskRuntime waits for task approval. Local code always requires this
   approval, including when the legacy task-approval toggle is disabled. Approval is
   task-scoped, not a new per-tool prompt or permission registry.
5. AgentTaskExecutor forwards the immutable snapshots through
   AgentCommandService.RunPanelAgentDirectAsync and RunServerAgentDirectAsync to AgentTask.
6. ServerAgentRunner only advertises admitted local snapshots. It preserves exact MCP
   tool casing; only legacy builtin names normalize case. Model-authored tool names
   cannot create a lease.
7. McpToolUseCase.ExecuteWithLeaseAsync revalidates the source identity, applies the
   existing ToolGate with an exact-name local FileOrShell exception, checks budget and
   schema, and calls the source's lease-aware dispatch. Worker dispatch checks the
   admitted numeric generation under its serialization gate before writing a request.

## Identity and revocation

The host broker supplies an opaque owner-incarnation plus process-generation token.
It must use the generation captured in the discovery snapshot, not re-read a mutable
counter after discovery. Reconstructing a source can restart its numeric counter at
one; a fresh owner nonce prevents old persisted tasks from authorizing that new source.
An old same-name lease is never upgraded to a current source after approval.
Disable, replacement, failed readiness and source-generation changes fail closed.
Approval resumes the original request; it does not rediscover or recapture authority.

Ordinary chat // and direct server calls do not supply these admitted leases. Local
Python remains unavailable on those paths; this milestone does not introduce a chat
approval bridge. Remote MCP policy remains independent. The process boundary is not
an OS capability sandbox: approved plugin code is trusted local code.

## Native evidence

Run from the bot source root, passing an operator-provisioned absolute Python path:

```text
dotnet run --project tests/BotAgent.PythonAgentProbe/BotAgent.PythonAgentProbe.csproj -c Release -- <python-executable>
```

The native probe uses actual product AgentTaskRuntime, internal AgentTaskExecutor,
AgentCommandService, ServerAgentRunner, McpToolUseCase and PythonWorkerToolSource.
Only the model HTTP port, session store and source-owner wrapper are synthetic. It
never opens a network socket, installs packages or accesses chat/member data. Its
worker writes a counted synthetic marker in a unique temporary test directory.

Observed on Windows with Python 3.13.12 and .NET SDK 10.0.101: **23 assertions pass**,
Release build zero warnings/errors. Cases cover pending approval with zero model or
worker effects; rejected approval with zero effects; approved exact-case execution once; serialized old admission versus
replacement sources both at numeric generation one; fresh admission after replacement;
central schema/budget denial; inherited/wrong-scope/wrong-case denial; legacy requests;
and the ordinary unleased server path.

This focused probe's source-owner wrapper is not the production package broker. The
independent acceptance probe must separately exercise the actual broker and installed
package/source lifecycle. It also owns its frozen binary hashes and original red
counterexamples. No Linux process-cleanup or external-human acceptance claim is made.
