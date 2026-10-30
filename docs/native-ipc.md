# Native IPC version 1

The local launcher/DLL ABI remains version **1**, cooperative rules are **4**, and network protocol is **4** (`T12L4` invitations). These version numbers have separate purposes. All three known executable profiles use this layout. The current behavior is defined by [the cooperative rules](cooperative-rules.md) and [engine integration](native-engine.md); executed tests belong in [verification/RESULTS.md](../verification/RESULTS.md).

Both offline and online startup use a suspended process and early injection. IPC carries startup metadata, controls and state digests. There are no video/audio capture or transport mappings. Integers below are little-endian 32-bit values unless identified as floats.

## Early-start metadata and readiness

The launcher creates `Local\TH12CoopBoot_<pid>` before invoking `injector --early` in either mode. It is 16 bytes:

| Offset | Meaning |
| --- | --- |
| 0 | Magic `0x42323154` |
| 4 | Version 1 |
| 8 | Primary thread ID returned by `CreateProcess` |
| 12 | Early-start marker, required value 1 |

The DLL verifies that this thread belongs to the injected process. If a network mapping exists, its primary-thread field must agree. Explicit early-start network diagnostics may provide the verified thread through the network mapping alone; ordinary offline startup requires the boot mapping. Missing/invalid metadata is rejected. Neither the remote peer nor a room code chooses a local process/thread to inject.

Retail installs all verified game hooks while the primary thread is suspended. Steam first publishes armed status 5, then the launcher resumes normal Steam startup. Installation occurs only at the known first seed call on the verified primary thread. Status 5 is not device readiness. The clock import is restored before hook validation/installation; offline preserves the native clock seed, online substitutes the agreed seed. There is no supported late title-screen injection path.

After successful installation, both modes expose `Local\TH12CoopReady_<pid>`, a 16-byte mapping with magic `0x52323154`, version 1, device-ready flag at +8 and a reserved error field at +12. The flag becomes 1 after the native D3D device pointer is initialized. Online also publishes that flag at network-header +88. A present mapping or installed-hook status alone does not prove the device is ready.

## Offline controls and common telemetry

The DLL creates the independent 64-byte `Local\TH12Coop_<pid>` mapping:

| Offset | Meaning |
| --- | --- |
| 0 / 4 | Magic `0x50323154`, version 1 |
| 8 / 12 | Managed P1/P2 gameplay masks |
| 16 | Flags: bit 0 managed controls; bit 1 smoke raw-menu-input override |
| 20 | Status: 0 initializing, 1 hooks installed/no second actor, 2 second actor exists, 5 Steam fence armed; high bit indicates initialization failure |
| 24 | P1 actor-pass counter; not the online frame sequence |
| 28 / 32 / 36 / 40 | P1 X/Y and P2 X/Y, IEEE-754 float32 |
| 44 | Launcher `GetTickCount()` control heartbeat |
| 48 / 52 | Native P1/P2 states |
| 56 / 60 | Active P1/P2 shot counts |

Managed offline gameplay uses bit 0 and masks `0xfb`; managed P1 menu/pause polling uses `0x1fb`. A heartbeat older than one second releases those controls. Keyboard and selected gamepad bindings are merged before publishing; native physical input does not bypass rebinding. Bit 1 can override raw P1 menu input only in the separate smoke build and has no override effect in the normal DLL. Without managed input, native P1 controls and the documented P2 keyboard bindings apply. Position/state fields can retain their last observation after actor teardown, so consumers must also inspect status. This telemetry has no frame-sequence publication guarantee and is not used for synchronization decisions.

## Independent player selection

Before injection the launcher optionally publishes a 16-byte `Local\TH12CoopPlayers_<pid>` mapping: magic `0x43323154`, version 1, P1 selection, P2 selection. Selection IDs 0–5 are Reimu A/B, Marisa A/B, Sanae A/B. The native DLL reads and validates it once. An absent mapping preserves the old in-game same-character selection for diagnostic callers; malformed mappings fail initialization. Protocol 4 first sends the guest selection as `[4, selection]` (`255` means the legacy menu path), then freezes both selections in the session manifest. The guest checks its own selection and acknowledges the complete manifest hash before either game starts. Selection and active bomb equipment are included in the native digest.

Online actor controls come exclusively from committed network input; the 64-byte mapping remains telemetry. Its status 2 means an actor pair exists, not that both actors are currently alive or movable.

## Online input and digest ring

The launcher creates `Local\TH12CoopNet_<pid>` before loading the DLL. Its 12,544 bytes contain a 256-byte header, 256 input entries of 16 bytes and 256 digest entries of 32 bytes. Slots use `frame % 256`; input frames start at 1. Native digest 0 describes initial state; the managed comparison API consumes completed frames starting at 1.

| Header offset | Meaning |
| --- | --- |
| 0 / 4 / 8 / 12 | Magic `0x4e323154`, version 1, byte size 12544, capacity 256 |
| 16 / 20 | Flags and negotiated RNG seed |
| 24 / 28 | Launcher `GetTickCount()` heartbeat and abort code |
| 32 | Native status: 0 initializing, 1 hooks installed, 2 waiting for input, 3 running, 4 failed, 5 Steam startup fence armed |
| 36 / 40 / 44 | Requested input frame, consumed input frame, completed digest frame |
| 48 / 52 | Latest digest low/high words |
| 56 / 60 | Native error and start time of the current input wait |
| 64 / 68 | Committed P1/P2 masks |
| 72 / 76 | Native simulation thread ID and required cooperative rules version 3 |
| 80 / 84 | Protocol input mask converted from the local device word (`physical & 0x1fb`) and its frame number |
| 88 | D3D device initialized |
| 92 | Primary game thread ID from `CreateProcess`; agrees with boot metadata |
| 96..116 | Diagnostic component hashes: globals, P1, P2, enemies, bullets/items/lasers, menus |

Flag 1 enables lockstep. Flag 2 additionally namespaces the original `Touhou 12 App` singleton mutex for the explicit same-PC test harness. Production online sessions do not set flag 2. Other bits are rejected.

Input entries start at offset 256 and contain `sequence, p1, p2, flags`. The launcher writes data first and publishes sequence last. A commit must be exactly the next local commit, requested frame and consumed frame +1; duplicates, ahead-of-request writes and invalid masks are rejected. P1 permits `0x1fb`, P2 permits `0xfb`, and entry flags must be zero. Bits are shot/confirm 1, bomb/cancel 2, focus 8, up/down/left/right `0x10/0x20/0x40/0x80`, and pause `0x100`. A guest pause request is merged into P1's raw mask deterministically using the preceding completed digest's actor-existence state. Guest Escape is not a P2 gameplay bit; other menu choices belong to P1.

Digest entries start at offset 4352 and contain `sequence, hashLow, hashHigh, scene, stage, playerStates, gameFrame, gameplayRngSeed`. Scene comes from native `0x4CEE3C`. The low/high bytes of `playerStates` contain P1/P2 states; 255 means no actor. Native publishes digest sequence last. Consumers verify a stable sequence before accepting an entry. Full 64-bit hashes are compared; header component hashes are diagnostic aids, not substitutes. The hash includes the enumerated native state and cooperative state such as reserve credits, item owners, homing references and delayed bomb-effect ownership. It is not a complete-memory equivalence or anti-cheat proof.

Optional auto-reset events `Local\TH12CoopNetRequest_<pid>` and `Local\TH12CoopNetCommit_<pid>` wake consumers. They carry no data and do not replace sequence validation. Missing events use bounded polling. The barrier never predicts or repeats missing input. Launcher heartbeat loss is detected after 5 seconds; first input has a 125-second deadline, later input 40 seconds, and resource joins 30 seconds. Managed gameplay input/request waits use 35 seconds and TLS record receives 40 seconds; shared startup uses a 120-second budget. Failure stops simulation until the launcher closes its owned process. There is no resume/rejoin recovery protocol.

Native errors 1–7 cover heartbeat/abort, input timeout, unstable input sequence, invalid masks, an input ahead of the expected frame, invalid mapping, and mutex-hook protection failure. Errors 8–10 cover a failed resource wait, resource timeout, and ANM upload failure. Errors 11–14 cover invalid enemy/laser list bounds, curve data, and unknown laser type. Error 15 during fingerprinting means ECL structure/range/work-budget validation failed. The startup fence also uses 15 as described below; interpret the number with its logged phase. An explicit launcher abort can publish its own error code.

Errors 15–17 cover a wrong-thread/reentered Steam seed fence, inability to arm/restore its clock import, and game-hook installation failure at that fence. Status 5 permits normal Steam startup to resume; it does not authorize commits or advertise device readiness. The exact return address and verified primary-thread identity gate installation, as described above. Offline failures are also reported through the 64-byte status field because offline has no network mapping.

## Smoke-only destructive audit commands

Only `th12coop-smoke.dll` creates `Local\TH12CoopAudit_<pid>`. Its 256-byte layout is magic `0x41423154` / version 1 at +0/+4, command sequence/completed sequence at +8/+12, family/case/result/reserved at +16/+20/+24/+28, and 32 detail words at +32. The rest is reserved. Publish family/case first and sequence last. Commands execute on the game thread before P1's actor pass; completion is published after result/details.

Families 1, 2 and 3 exercise resources, targeting/collision and items/progression using fixed native-state mutations. Family 5 probes synchronization fields, pointer exclusion, real Ex acceleration, native input conversion and the ANM lock with a second thread. Family 4 configures a persistent campaign fixture and allows ticks to continue; its diagnostic counters can change after command completion. Close the disposable fixture process after observation. The normal DLL exposes none of this command mapping or its mutation handlers. These commands are local diagnostics, are not carried in the network protocol and must not be described as input-only tests. Actual executed coverage is recorded separately in [verification/RESULTS.md](../verification/RESULTS.md).
