# Items, UFOs, progression and presentation audit

Audit date: 2026-10-29. Primary evidence is the user's retail 1.00b executable, SHA-256 `d8d644d2e64957a3031b1a1399d0502e1ddaa5252d2c4e492770ad6717827628`. Addresses below are **1.00b**; the existing verified layout translates the corresponding 1.00a addresses by +0x20. Steam's on-disk code is wrapped, so Steam signatures are checked only after its native bootstrap. This document records static evidence and implementation scope; [verification/RESULTS.md](../verification/RESULTS.md) is the authoritative record of executed checks and their build-specific results.

Reproduce the read-only evidence with `research/audit-items-progression.py`. It emits scoped disassembly and all 116 direct absolute Player singleton references into `research/audit-items-*.asm` and `research/audit-items-xrefs.json`. Function names are hints from th-re-data; an unnamed helper can inherit the preceding named function's label. The disassembly, not that label, determines the behavior described here.

## Item ownership and collection

The native ItemManager tick runs **once**, over 2664 slots at `manager+0x14`, stride `0x9d8` (`0x425C47` and `0x426F5E`). The pool contains 600 ordinary items, 16 UFO/token/reward slots and 2048 cancel items. Both players share score, power, life/bomb reserves, fragments, point-item value and UFO inventory. Each physical item is consumed and awarded once.

The previous implementation selected only the nearest actor before an item moved. Four correctness problems follow directly from the code:

1. Focus attraction reads global input bit 8 at `0x426E0B`. Swapping only the Player singleton made P2's focus attraction depend on P1's controls.
2. Choosing the nearest actor suppresses auto-collection when the farther actor is above the collection line and the nearer actor is below it.
3. A nearest-center test is not the union of pickup rectangles. An item can lie inside one actor's rectangle near a corner while the other actor is closer but outside an edge. Rechecking after item motion is also necessary.
4. Choosing a new target every frame lets attracted items reverse target as players cross. Ownership must persist until consumption, target unavailability or slot reuse.

`src/native/coop_items.h` implements separate motion-target and post-motion pickup selection. It scopes the current input word to the selected actor and restores all five native input words and the singleton after the manager returns. The masks come from the controls actually consumed by the actors: native Player priority is 16 (`0x435B8E`), ItemManager priority is 22 (`0x42597F`). It does not sample keyboard input a second time.

Rules:

- An eligible collector is in native state 0 or 1 and passes the shared actor-eligibility callback. State 0 is respawn/entry movement, not a cutscene. State 2 is committed death and state 4 is the deathbomb window; neither collects.
- Ordinary falling items auto-collect if either eligible actor is above Y=128. Native `0x425CF5..0x425D31` checks this line and sets state 3. If both qualify, the nearer actor wins; exact ties select P1.
- Attraction in states 3, 4 and 8 latches its target. A target that becomes ineligible is replaced by an eligible actor. A POC event upgrades state 4 to full-speed state 3 while preserving its owner and native full-value scoring.
- Physical pickup tests both eligible actors' native rectangles after movement. Only one native award branch is entered. Local focus attraction uses each actor's own focus-dependent rectangle.
- With no eligible collector, ordinary player attraction is released and item movement continues. POC, cancel-item apex attraction and token proximity effects are skipped until an eligible actor exists. Large-UFO suction continues once.
- The sidecar stores only actor IDs. Native timer-zero slot initialization clears stale ownership. Explicit reset is also required for actor creation, stage/retry activation and destruction; manager replacement resets automatically. The owner array is included in the synchronization digest.

Native bounds are at Player `+c444/+c448/+c450/+c454` for pickup, `+c45c/+c460/+c468/+c46c` for focused attraction and `+c474/+c478/+c480/+c484` for unfocused attraction. Player tick writes these bounds from its current position. The common post-motion pickup entry is `0x4260AF`; its no-collector continuation `0x426ECB` starts animation/timer work with an empty x87 stack.

Native item states observed: 0 inactive, 1 falling, 2 rising cancel/drop item, 3 full auto-collection, 4 local attraction, 5 delayed cancel spawn, 6 UFO token, 7 large-UFO suction, 8 forced UFO reward homing. These are item states, distinct from Player states.

The native pickup dispatch table at `0x426F7C` maps types 1 small power, 2 point, 3 large power, 4 life fragment, 5 bomb fragment, 6 life, 7 bomb, 8 full power, 9 point-value bonus, 10–15 UFO tokens, and 16–18 UFO rewards. Native reward arithmetic and caps execute once. Power changes call the existing paired option-rebuild hook. Point values at `0x426B10` use the actual collector's height, with full value for state 3; they do not use the other actor's height.

## UFO behavior

Token color control is separate from collection. `0x42628F..0x426352` computes squared distance from the selected Player and compares it with **3600 (radius 60)**. Nearness freezes token lifetime/color timers by subtracting their upcoming increment. Choosing the nearest eligible actor implements “either actor near the token freezes its color”; no eligible actor takes the native far-token branch. Token physical pickup uses the same post-motion rectangle union as other items.

`0x4270B0` receives one color and one collected item. It appends to the global inventory through `0x422E80`, or applies the native full-inventory value bonus/cap. No second call is added. `UFO::tick` at `0x44A2D0` reads that shared inventory and spawns through `0x44A8A0` once. The UFO block `0x449FA0..0x44B3E0` has no direct Player singleton accesses. Its spawn power ratio, food totals and rewards use team globals. Suction helper `0x4271C0` assigns state 7; native absorption at `0x426978` consumes the item and invokes `0x44ACA0` once. UFO cleanup resets the shared token array once.

No player-specific UFO inventories, duplicated food awards or duplicated UFO spawns are introduced. Increased team shot damage can kill a UFO sooner; that is a team-combat rule, separate from the duplicated shared-bomb damage defect found by the resources audit.

## Dialogue, transitions and GUI

The unnamed helper `0x41D020..0x41D06B` is actor-local despite inheriting the map label `Gui::update_bombs`: it sets Player `+c414` bit 8, dispatches interrupt 3 to two ANM IDs for each of eight options, and clears Player `+c418`. Dialogue calls it at `0x420A1B`. Calling it with only P1 left P2's options/transition state out of sync.

`src/native/coop_progression.h` wraps **this helper only** for both actors. It does not execute the dialogue VM or GUI update twice. The direct singleton reference at `0x42014B` is used for the shared character portrait's ANM data; a shared portrait is appropriate because the current game selects one team character/shot type. ECL script-player movement and getters are covered by the enemy/script audit, not inferred from the dialogue VM's direct references.

Four GUI occlusion regions must account for either eligible actor independently:

| Region | Native entry | Fade condition | Keep-faded condition |
|---|---|---|---|
| Lower-left counters | `0x41E171` | X < -64 and Y > 416 | X <= -64 and Y >= 400 |
| Upper-left boss labels | `0x41E4CD` | X < -64 and Y <= 64 | X <= 0 and Y < 80 |
| Bottom boss marker | `0x41EE3B` | Native opacity from horizontal distance | Use minimum eligible actor distance |
| Spellcard overlay | `0x40DBC9` | Native upper-screen threshold | Minimum eligible actor Y; native 128 leave threshold |

The progression header selects a GUI-local pointer for each predicate. It retains native fade/animation behavior and does not change the global aiming target. Callouts occur before each calculation's first `FLD`; inserting a C++ call at the later singleton load would corrupt a live x87 stack. When neither actor is eligible, a non-occluding visual position restores normal visibility. Shared hint/popup placement remains team/P1 presentation; no reward is attributed twice.

## Progression, records and validation scope

| Area | Static evidence and intended behavior | Status/limit |
|---|---|---|
| Stage initialization | `0x421CD0` owns the one world; creates Player at `0x421EA4`, then shared managers. Per-shot play count increments once at `0x421F4B..0x421F6B`. | Keep one world and one record update; paired actor constructor already applies. |
| Start/retry/continue reset | `0x422874` and `0x422987` call Player activation `0x436100`; the existing activation wrapper applies native initialization to both actors. Shared score/resources/UFO globals are initialized by GameThread outside that actor-local helper. | Root resources layer must also reset waiting/reserve sidecars. Item ownership must reset here. |
| Teardown | GameThread destructor at `0x42242B` destroys the singleton; existing paired destructor releases P2 before P1 while preserving shared ANM ownership. | Teardown/re-entry regression evidence is tracked in [verification results](../verification/RESULTS.md). |
| Extra | `0x421D7D..0x421D8B` maps stage 7 to difficulty 4; it uses the same world/actor constructor. | All team character/shot choices are exposed by the menu-access view below. Entry/retry and full-clear checks have distinct scopes; consult [verification results](../verification/RESULTS.md). |
| Practice | `0x421E6D..0x421EA4` uses mode bit 0x10 and practice-lives global `0x4B0CEC`; the same actor constructor follows. | Stage selection, retry and end-screen flow are separate coverage areas recorded in [verification results](../verification/RESULTS.md). |
| Ending | `0x410D76..0x410DEE` chooses the ending using team character/shot and shared continues count. It writes ending-seen/clear bytes once in the shared Scorefile. | Static analysis alone does not establish a completed ending path; execution evidence belongs in [verification results](../verification/RESULTS.md). |
| High scores/unlocks | The native Scorefile and team score remain one record stream; ending and GameThread record mutations are not duplicated. | Cooperative score/unlock policy must be documented separately from original solo records. |
| Replays/demos | Existing replay save hook declines cooperative runs because native replay format records only P1. Existing replays and demos deliberately create one actor. | Do not present native replay saving as supported 2P playback. In-memory replay bookkeeping remains native. |
| Online persistence | `OnlineProfile.Prepare` starts a clean per-session CWD and copies no scores or replays. Both peers therefore start from equal history. | High scores/ending records earned in one online session are not loaded into the next. Cooperative access to Extra/practice is independent of those records. |

### Explicit cooperative stage access

Cooperative mode exposes Extra for all six character/shot combinations and all six practice stages for all four ordinary difficulties, offline and online. The original solo clear/ending/score data is not overwritten to pretend those stages have been cleared.

The progression header refreshes a **menu-query view** of the existing Scorefile before the native MainMenu tick. Only that view has Extra-access bits `+1e9d0..+1e9d5 & 0x10` and practice-access bytes `+5b1 + shot*45f4 + difficulty*48 + stageIndex*8` enabled. Nine verified menu reads are redirected: `0x43F180` (title Extra cursor eligibility), `0x4401B6`, `0x440676`, `0x4409B6`, `0x440D06` (title Extra styling), `0x445301` (Extra character choice), `0x445A8A` (Extra shot choice), `0x44612A` (practice confirmation), and `0x446430` (practice-stage labels). The real singleton remains unchanged, as do record displays/writes outside those queries and demo/replay gameplay. `research/audit-items-access-signatures.json` and `research/audit-items-menu-access.json` record both retail variants. Current Extra/practice navigation, retry and teardown execution evidence is maintained in [verification/RESULTS.md](../verification/RESULTS.md).

The first live Extra navigation test exposed the separate cursor-eligibility helper: title initialization calls `0x43F180` at `0x43FE02`, and a false result appends index 1 to the cursor's disabled list at MainMenu `+b8` / count `+fc`. Styling the Extra entry alone therefore leaves selection skipping directly from 0 to 2. The helper's only direct caller is that title initialization; its Scorefile read now uses the same query view. The fixture's case 8 additionally executes the installed native helper and checks that the real Scorefile remains unchanged. Menu state/substate/cursor offsets `+1c/+24/+28` are correct for title, rank, character, shot and practice. The mode runner records each held/released cursor, disabled list, raw input and both original/query Extra flags for regression diagnosis.

The audit does **not** claim all stages, scripts, endings, Extra, practice and continue combinations have been played through. [verification/RESULTS.md](../verification/RESULTS.md) tracks the executed coverage and remaining limits for P2-only focus attraction, farther-player POC, pickup rectangle union after movement, owner retention/retarget, both-unavailable token behavior, one shared token/food reward, paired transition flags/options, GUI occlusion, and lockstep synchronization with the owner digest. These execution records, rather than this static audit, determine which checks passed for a given build. All new signatures are independently recorded in `research/audit-items-hook-signatures.json` and `research/audit-items-progression-signatures.json` for both retail versions.

### Full campaign diagnostic fixture

`src/native/coop_audit_campaign.h` is compiled only into the smoke DLL. Its family-4 audit command is a persistent fixture, so it permits the normal actor/world ticks to continue after configuration. Call `coop_campaign::Configure(caseId, details32)` on the game thread with both initialized actors in state 1 and the native option/invulnerability trampolines installed. Call `BeforeFrame()` before P1's normal tick and apply `ScaleDamage()` to the final cooperative damage result. Case 1 uses damage ×4; case 2 retains ordinary damage. Both cases reset native invulnerability to 60 each actor tick, set team power to 400 and refresh native options once per stage/actor pair. Native timer speed remains 1. There is no stage, enemy-HP, ECL-PC or player-position write. The process must be a disposable fixture and must be closed afterward.

Global timer acceleration is invalid for this fixture. Retail 1.00b `GameThread::on_tick` at `0x422963` compares its timer with **exactly 30**, then at `0x42296C..0x42297F` checks and clears delayed-initialization flag `0x800` before activating actors and enabling the next stage's managers. A speed-4 sequence of 0, 4, 8, … skips 30. The first campaign diagnostic reached stage 2 with that flag still set and its native stage timer stuck at zero; this was a fixture defect, not evidence that the production transition hook failed. Another equality gate at `0x422A5F` checks exactly 5. Retaining native timing preserves these transitions rather than changing their counters or script state.

`scripts/test_cooperative_campaign.py` creates a fresh isolated copy with no score/replay import, navigates the real menus, drives both actors with their controls, follows the native boss X position, and advances native dialogue with shot edges. The boss pointer at EnemyManager `+1c` is a full Enemy, verified by the GUI's `0x41EBC2/0x41EBCD` accesses to `+26f8`; position is `+1074/+1078`, HP `+2648`. Observations are read-only process-memory snapshots; only the documented input/audit mappings receive commands.

Completion criteria are deliberately native and distinct from game over:

- Story: observe stages 1, 2, 3, 4, 5 and 6 in order, both actors present at each stage, then a live Ending singleton at `0x4B43D8` with a loaded ending script. Retail 1.00b `0x410D76..0x410D92` computes the expected no-continue ending index as team shot index +6; the test checks that value.
- Extra: observe stage 7 and the native clear result. Retail 1.00b dialogue `0x420C75` calls `0x433870`, which enters PauseMenu state 21 and sets `+1f4=1` at `0x433987`. The miss/game-over entry sets that flag to zero at `0x433838`. The test accepts the clear-result state family 20–26 only when the clear flag is one.

The runner has a default 40-minute limit per campaign and writes stage/scene/dialogue changes, boss HP, actor telemetry, GameThread timer/flags, fixture configuration and executable/DLL hashes to JSON. Outside native loading, dialogue, pause and ending states, a stage timer or active callback counter that does not advance for 20 seconds produces a diagnostic failure. These invulnerable runs with optional extra damage can verify progression and object lifetime paths; they are **not normal-difficulty playthroughs**, do not establish balance, and do not cover every ending/continue branch. Creating these fixtures and syntax-checking the runner does not itself constitute a runtime pass.
