# Reply prompt pacing probe

Pure synthetic regression checks for the opt-in reply-pacing prompt boundary. The probe references only Model and Core, fixes its clock/culture, rejects HTTP and image I/O, and stops the model call at request assembly. It never starts a bot or opens business data.

Coverage:
- Seven whole-prompt SHA-256 fixtures from baseline `b80d7b2` remain pinned, including all four legacy desire bands, threshold interpolation, and private replies. The later silence repair must add its exact zero-message contract once; after removing that explicitly allowed addition, every original hash must still match.
- Paced prompts differ only by removal of the legacy desire sentence; suitability thresholds, content/safety rules, tool instructions, vibe, and stable cache prefixes remain unchanged.
- Every sampling branch (including adaptive sampling disabled) propagates the immutable mode flag without changing temperature, top-p, intent, or reason.
- The model facade uses the admitted turn flag despite changing legacy desire and atmosphere-damping settings; false/missing flags retain legacy damping.

Run from the worktree root with isolated output. The restore source below is local-only; these two production projects have no external package dependencies.

```sh
dotnet restore tests/BotAgent.ReplyPromptProbe/BotAgent.ReplyPromptProbe.csproj --artifacts-path tests/BotAgent.ReplyPromptProbe/.artifacts --source .
dotnet run --no-restore --project tests/BotAgent.ReplyPromptProbe/BotAgent.ReplyPromptProbe.csproj --artifacts-path tests/BotAgent.ReplyPromptProbe/.artifacts
```
