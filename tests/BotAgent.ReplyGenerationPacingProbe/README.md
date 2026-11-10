# Reply generation pacing probe

Runs the real `AgentTurnLoop` with a synthetic `IModelClient`, without HTTP, protocol connections, production data, clocks, or sleeps. Also exercises shared prompt-note retention and ambient commit scopes.

```sh
dotnet run --project tests/BotAgent.ReplyGenerationPacingProbe/BotAgent.ReplyGenerationPacingProbe.csproj -c Release
```

This project references the Engine project: coordinate its build with other .NET builds using this worktree. Unlike the standalone coordinator probe, it shares Engine build outputs.

## Assertions

- A soft draft exposes no tool flags, sticker choices, or natural-task hint, invokes one model step, and never invokes the inline callback.
- Every model completion is validated before inline dispatch or result return.
- A stale completion raises the fixed pacing interruption, executes no inline action, and preserves charged model usage.
- Awaiting quiet retains the completed draft rather than invoking the model again.
- Music, recall, search, and link notes, including a missing note, are consumed once across retries.
- Both pacing-mode values survive `AppSettings.ToSamplingConfig` and disabled adaptive sampling.
- The legacy null-pacing loop keeps its step count and natural-task hint.
- Nested commit scopes restore their parent and do not leak into legacy operations.

This probe does **not** prove the integrated scheduler releases the tenant queue while waiting, transport atomicity, official uploads, source retirement, or complete all-segment delivery. Those require the coordinator, delivery, and integrated scheduler checks; no production acceptance is claimed.
