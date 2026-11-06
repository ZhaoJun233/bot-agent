# #151 configuration regression repair evidence

Date: 2026-11-06 (Asia/Shanghai). Scope: ReviewRemediationProbe only.

## Result

- Final native Windows/.NET SDK 10.0.101 forced rebuild: exit 0, zero errors, three existing CS0649 warnings.
- Final complete probe: **Passed 1679; failed 0**, exit 0.
- Final isolated configuration-module-atomic-fault child: **passed 6; failed 0**, exit 0.
- Complete probe also confirms future-module child 4/0, host child 21/0, configuration HTTP child 27/0.
- No product defect identified for these failures; no product files changed. No commit, push, deployment, or private runtime-data access.

## Original failures and diagnosis

The supplied candidate suite log (evidence E01) reported the exact FieldIds 1..147 failure and configuration-module-atomic-fault child failed=1/passed=3. A fresh forced native baseline reproduced **Passed 1149; failed 2**, exit 1. The isolated baseline reproduced **CFG_CHILD failed=1 passed=3 type=InvalidOperationException**, exit 1.

1. The #151 contract intentionally appends IDs 148–152: ReplyPacingEnabled, GroupInterjectionProbability, GroupInterjectionCooldownSeconds, GroupContinuationWaitSeconds, PrivateContinuationWaitSeconds. The probe still required exactly 1..147. The related direct-field and descriptor counts were 136 instead of 141, and legacy scalar count was 135 instead of 140.
2. Physical schema migration v14 intentionally widens the allowed field IDs before configuration-module installation. The fourth atomic-fault assertion still required physical schema 13. Its preceding three assertions passed: the expected initialization failure leaves the database not ready, configuration tables roll back, and the module marker rolls back. The physical migration is a separate committed transaction; expecting exactly 14 retains that boundary.
3. The first repair run exposed the previously skipped legacy scalar assertion: **Passed 1347; failed 1**, while the atomic child was already green. Updating the exact scalar count to 140 completed the repair.

## Scoped changes

Only [ConfigurationContractTests.cs](<ConfigurationContractTests.cs>) and this evidence record were authored for this task.

- Exact sequential FieldIds: 1..152; no lower-bound assertion or accepted range of alternatives.
- Exact seven-partition typed fields, unique descriptors, and HTTP field descriptors: 141.
- Exact legacy scalar count: 140; all missing edits must remain Keep and existing composite exclusions remain intact.
- Exact committed physical schema after injected module failure: 14.
- Strengthened the fault catch to require SQLite extended error 1811 (SQLITE_CONSTRAINT_TRIGGER), preventing unrelated SQLite failures from masquerading as the injected module fault.
- Preserved all six atomic checks, including not-ready state, absence of module tables and marker after rollback, and clean retry.

Product contracts inspected read-only: [FieldIds](<../../src/BotAgent.Core/Domain/Configuration/ConfigurationFields.cs>), [typed changes](<../../src/BotAgent.Core/Domain/Configuration/ConfigurationChanges.cs>), [legacy scalar mapper](<../../src/BotAgent.Headless/Adapters/Panel/PanelLegacyScalarFields.cs>), and [physical/module migrations](<../../src/BotAgent.Storage/AppDatabase.cs>).

## Native commands

Run from the repository root. The unique artifacts directory avoids shared binaries. Every source change was followed by `-t:Rebuild`, because native CAS preserves source mtime.

```sh
dotnet build tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release --artifacts-path .tmp/config-regression-repair -t:Rebuild -m:1 -nr:false
dotnet .tmp/config-regression-repair/bin/BotAgent.ReviewRemediationProbe/release/BotAgent.ReviewRemediationProbe.dll --configuration-module-atomic-fault
dotnet .tmp/config-regression-repair/bin/BotAgent.ReviewRemediationProbe/release/BotAgent.ReviewRemediationProbe.dll
git diff --check -- tests/BotAgent.ReviewRemediationProbe
```

Native absolute OPS reads and revision-checked CAS edits were used; generic read was not used. Codegraph was consulted once and timed out. Raw full-suite logs contain non-UTF-8 bytes, so bounded native OPS hex reads were decoded locally to extract only ASCII assertion/status summaries; arbitrary fixture payloads were not emitted. Original raw logs are retained unmodified.

## SHA-256 fingerprints

Paths below are relative to the repository root.

| Artifact | SHA-256 |
| --- | --- |
| Supplied candidate log (evidence E01) | `4d570ad4acb748e6358dc447b8903357b946887504599362b8804a2ebe0ccd5d` |
| Probe source before this repair (historical) | `d151f9885ec979d96a50204f3c3add3d25967f02bfbd8f0437d884ba83f58243` |
| [Final probe source](<ConfigurationContractTests.cs>) | `53fd001dfecc22f62cf71095c80986d6d6b878147079f3aadd8a7cabc9cdaae7` |
| Baseline forced build log (evidence E02) | `f45293d84130a187fa413c955230d9714b1b1ad2229bac27538cbaeba4535849` |
| Baseline full-suite log (evidence E03) | `e13352bddc170aa87cdc8b3daf510ecb53750663e23254f4c2081ce7c34410b5` |
| Baseline atomic log (evidence E04) | `96dea97c4fef009477da64ade4dd02a1d0e703365dfb30ca3f5708c2a8a9ca8f` |
| Intermediate full-suite log (evidence E05) | `d46421237edbebb3506f9bdd1f0d99468e17165d5f8cf3bd532d5d6234d0d5b6` |
| Final forced build log (evidence E06) | `a2d90215507a366dfb1b931361719f23871cf24300201c58a1893577ed3257a4` |
| Final full-suite log (evidence E07) | `5f86cb1d08ee5800f59280e08dbccb45ff93ba501e5d39f6aba167ecb93fc922` |
| Final atomic log (evidence E08) | `2bc04034cc4a2ade5b56e9a49841b85d71843c47c2188fdb13cdf894e976f2ff` |
| Baseline probe DLL (historical, replaced by forced rebuild) | `5bb0a5676f8f643bc1dbe0c51d4fd99433014cfcbbdcc4ee9323125d35549539` |
| Final probe DLL (evidence E09) | `160aeed65bf5e63353d81b7eb0358a54eef6da678d3a30904822717253221a0d` |
| Final Core DLL (evidence E10) | `18a9b03ed22695ab023a12c2a78cfefbda87d65bba2064fde369acb7b946707e` |
| Final Storage DLL (evidence E11) | `65fb31c726e2d46fc190cd49c9318c25f235bbac7aefe8208ac89631921b5c96` |
| Final Headless DLL (evidence E12) | `348959962ba1559d3acf2b7d667b53f8cbab601c9bda430f517c3645a61676c9` |

## Shared-task coordination

The delegated SDK did not expose shared-task list/get/claim/complete operations; two connector searches returned no matching tools. Lead was notified before edits and at completion and asked to perform task-10 bookkeeping. Technical work and native verification are complete; this record does not claim that shared-task bookkeeping succeeded.

## Local evidence index

Machine-local, ignored evidence is not part of a clean checkout. Labels above map to repository-root-relative paths below; original logs and hashes are unchanged. These are evidence locations, not a claim that a clean checkout contains the artifacts.

```text
E01  .tmp/final-verification-2/ReviewRemediationProbe.log
E02  .tmp/config-regression-repair/build-before.log
E03  .tmp/config-regression-repair/suite-before.log
E04  .tmp/config-regression-repair/atomic-before.log
E05  .tmp/config-regression-repair/suite-after.log
E06  .tmp/config-regression-repair/build-final.log
E07  .tmp/config-regression-repair/suite-final.log
E08  .tmp/config-regression-repair/atomic-final.log
E09  .tmp/config-regression-repair/bin/BotAgent.ReviewRemediationProbe/release/BotAgent.ReviewRemediationProbe.dll
E10  .tmp/config-regression-repair/bin/BotAgent.ReviewRemediationProbe/release/BotAgent.Core.dll
E11  .tmp/config-regression-repair/bin/BotAgent.ReviewRemediationProbe/release/BotAgent.Storage.dll
E12  .tmp/config-regression-repair/bin/BotAgent.ReviewRemediationProbe/release/BotAgent.Headless.dll
```
