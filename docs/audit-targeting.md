# Two-player targeting and external player-reference audit

Date: 2026-10-29. This audit reads the installed retail 1.00b executable
(`d8d644d2e64957a3031b1a1399d0502e1ddaa5252d2c4e492770ad6717827628`).
Code addresses in this document use **1.00b** notation. The verified retail
1.00a code addresses are `-0x20`; global/object offsets are unchanged. Steam's
authorized runtime game code matches retail 1.00b as recorded in the version audit.

## Inventory and limits

The complete `.text` range was scanned for all bytes encoding `PLAYER_PTR =
0x4b4514`. All **116 occurrences** decode as real direct memory operands; no
occurrences were found elsewhere in the mapped image. Every occurrence has an
instruction, surrounding disassembly, nearest database symbol, category and
remaining uncertainty in `research/audit-targeting-classified.json` and the
readable `research/audit-targeting-inventory.md`.

This is exhaustive for direct operands containing that address. It is not proof
of every indirect alias, computed pointer, script or runtime call path. Function
names in the third-party database are incomplete: for example, many apparent
references inside `EnemyData::constructor`, `load_sht_file` or `EclStack::ecl_return`
actually belong to following unnamed functions. The inventory retains the raw
site and a separate padding-boundary candidate to avoid treating the nearest
symbol as an authoritative enclosing function.

| Classification | Direct sites |
| --- | ---: |
| Bomb owner/origin/damage effects | 22 |
| Enemy ECL target coordinates/distance/direction | 10 |
| Generic angle-to-player helper | 1 |
| Bullet spawn proximity suppression | 1 |
| Damage state, damage source, homing choice/lifetime | 8 |
| Collision geometry, laser hit responses, graze effects | 13 |
| Item recipient/attraction | 12 |
| Player shots and options | 11 |
| Player accessors, lifecycle, debug lifecycle, shared reset | 21 |
| HUD proximity, dialogue, hint, popup, replay | 14 |
| Forced player movement | 2 |
| ANM/effect damage owner | 1 |

There is no direct player reference in Stage camera/STD code in this executable,
nor a direct call from that code to the inspected angle helper. That negative
result does not establish that every stage script is independent of the player;
ECL coordinate variables still influence enemy and stage-specific behavior.

## Targeting semantics and implementation

`src/native/coop_targeting.h` implements a separate **aim context**, never a swap
of the original `PLAYER_PTR`. It contains real player pointers only; no fake
Player object is exposed to native code.

- At each enemy ECL invocation, choose the nearest eligible player to the enemy's
  position (`Enemy + 0x1074/+0x1078`), with P1 winning an exact tie. Eligibility is
  supplied by the cooperative actor lifecycle policy. If neither is eligible,
  retain P1's defined coordinates. Freeze that choice until this ECL invocation
  returns, including nested firing operations; restore the caller's context.
- A volley outside an ECL invocation chooses from its shooter origin once.
  Preserve the native fan/ring count, angle spacing and speed. The original code
  computes one aimed angle before looping over the entire volley.
- An explicit bullet/laser re-aim operation outside either scope chooses the
  nearest eligible player from that operation's source position. It does not
  continuously turn a previously fired straight bullet or duplicate a volley.
- Item attraction retains the recipient already selected by the item policy.
  Its three calls to the generic angle helper are explicitly exempted from enemy
  targeting and use the actual current player context.
- Bullet spawn-distance suppression uses the nearest eligible player to the
  actual spawned bullet position, independently of the aim target. The native
  squared-distance comparison therefore rejects a spawn too close to either
  eligible player. With neither eligible, it takes the native non-rejection
  branch. The callback saves/restores the full FPU/SSE image and uses a separate
  empty x87 stack with the original control word during selection.
- Target selection adds no RNG consumption. The temporary context is restored
  after each invocation; the hash contains only target identity and scope depth,
  never an address.

Ten proven ECL coordinate loads and the angle helper's coordinate load use a
register-preserving CALL substitution. Outside an aim scope the loaders return
the actual original player pointer. They preserve flags, non-result registers and
the in-progress x87 stack. They do not redirect state, ownership, damage, collision,
item, bomb or scripted player-position writes.

| Hook boundary (1.00b) | Verified convention | Preserved behavior |
| --- | --- | --- |
| `0x468d90` `Enemy::ecl_run` | `EDI = Enemy*`; one stack float; `ret 4`; integer EAX result; ECX preserved | Executes original main/asynchronous ECL contexts once; wrapper only scopes target |
| `0x40b5e0` volley creation | `ESI = BulletShooter*`; no stack arguments; integer EAX result | Native shooter loop and cached base angle run once |
| `0x4377a0` angle-to-player | `ECX = float3*` source; float result in x87 ST0 | Original atan2 implementation and its coincident-position behavior |

The safe ECL loads are `0x41762a`, `0x4193ef`, `0x419ac8`, `0x419adc`,
`0x419d54`, `0x41a0b2`, `0x41a0c1`, `0x41a24a`, `0x41a25a`, `0x41a402`.
The generic angle helper loads at `0x4377a9`.

The generic helper's 15 direct callers are:

| Call sites | Purpose |
| --- | --- |
| `0x40ab83`, `0x40ac7c`, `0x40b208`, `0x40b4ad` | Bullet extra-instruction angle evaluations |
| `0x40b5ef` | Initial volley base angle |
| `0x40bc64`, `0x40c1ab` | Bullet turn/re-aim instructions |
| `0x41a0d7`, `0x41a2e7`, `0x41a2f6` | ECL angle/global evaluation |
| `0x425f59`, `0x42601a`, `0x4261ad` | Item attraction; preserve item recipient |
| `0x428bff`, `0x42d142` | Laser angle initialization/re-evaluation |

All three hook prologues, eleven coordinate-load instructions and the independent
spawn-distance load match both retail executables (30 comparisons), and all six version-specific item-call
exemptions resolve to the expected helper. This is static verification; build and
gameplay results belong in the coordinated verification record.

## Separate cooperative decisions required by the reference inventory

These references must **not** be redirected to an enemy's aim target. The broader
cooperative integration owns their policies and tests.

| Sites (1.00b) | Verified effect and required distinction |
| --- | --- |
| `0x40a6c0` | `shoot_one` compares squared distance from bullet position to player against shooter `+0x60` and returns `-1` before activation when too near. Implemented separately from the aim scope: select the nearest eligible player with complete FPU/SSE preservation; no eligible player takes the native non-rejection branch at `0x40a710`. |
| `0x413cbb`, `0x413d90`, `0x413df9`, `0x413e47` | Player state and `+0xc414` flags alter enemy damage/spell restrictions. Applying an arbitrary aim target here can scale combined damage incorrectly. Exact special-spell flag meanings remain partly unresolved. |
| `0x414349`, `0x412660` | Native horizontal-distance/lock target selection at player `+0x8980/+0x8984`. `coop_homing.h` now applies the original comparison and lock ordering independently to each eligible actor. The executable skips when candidate absolute-X distance is less than or equal to the existing one, and the setter only writes while unlocked; this is not relabeled as a new nearest-target algorithm. |
| `0x41364b`, `0x43abf0` | Enemy destruction and a separate clearing helper remove the current player's target and target pointers in all 256 shots. `coop_homing.h` clears both player collections before the native cleanup continues, including inactive slots and the final slot. The second helper has no observed static callers/address-taken references, but is covered if reached. |
| `0x414036`, `0x41b56c`, `0x41b5cd` | Post-collision graze effect position. Aggregating two collision results and restoring P1 can place P2's effect at P1. |
| `0x429a22`, `0x42b034`, `0x42cbc0` | Line/infinite/curve laser collision result 1 passes player position to laser virtual method `+0x18`. This must use the actual hit player, not the aimed player. |
| `0x429a64`, `0x42b077`, `0x42cc06` | The corresponding result-2 graze effect position. Requires actual grazer identity. |
| `0x437d0a` (entry `0x437d00`) | Additional oriented geometry query used by `LaserInfinite::method_0` at `0x42c321`. Reads player position and hitbox half-extents (`+0x9e4/+0x9e8`); EAX position plus three stack floats, `ret 12`. The geometry hook queries both eligible actors and retains native hit/graze priority. It does not change the last collision effect owner. |
| `0x41ae37` and writer `0x412579` | Enemy force-field helper `0x41ae20` normalizes a 2D vector toward the field and calls `0x412570` to write native XY/fixed position and refresh eight option positions. `coop_force.h` now replaces only that player-local block: native D3DX normalization and native writer run for each eligible actor, while the shared bullet loop at `0x41af01` runs once. The mode-1 guard and no-P2 path stay native. No static direct call or address-taken reference to `0x41ae20` was found; normal-game reachability remains unresolved, so a dedicated fixture invokes the complete entry explicitly. |
| `0x40dbd7`, `0x40dc13`, GUI positions at `0x41e17e/1f2/261/433/ee47`, popup `0x43df10/0x43e201` | Shared display proximity/transparency/indicator behavior currently reads one player. An either-player readability policy is separate from enemy target selection. |
| `0x42014b` | Dialogue uses player `+0x10` loaded ANM asset, not player coordinates. Shared character/asset identity makes this different from a second aim target. |
| `0x41d022` | Shared transition helper sets player `+0xc414` bit 8 and interrupts option VMs. Requires a paired-player transition audit; exact transition caller purpose is not established here. |
| `0x469820` | Unnamed ANM/effect initializer at `0x469750`, address-taken at `0x4af1ac`, creates a player-owned damage source. Preserve effect/bomb owner context; exact registration remains unresolved. |

The old whole-enemy singleton-swap approach would mix these ownership/state
semantics with targeting. In particular the old damage hook assumes its first
pass uses P1, and would count P2 twice if entered while the native singleton is
already P2. Keeping aim reads separate avoids that class of error.

Several compiled accessors have no observed direct call or address-taken use:
`0x412600`, `0x412650`, `0x412680`, `0x4126b0`, `0x425840`, `0x427b50`,
`0x435950`, `0x435960`, `0x435970`, `0x4395f0`. They are recorded rather than
silently discarded. No runtime reachability claim is made for these helpers.

## Collision review and native fixtures

`coop_collision.h` was independently checked against both retail binaries: all
26 version-specific signature comparisons pass. Rectangle/circle/laser wrappers
preserve their original register/stack calling conventions. Laser cancellation
receives the actual hit position; simultaneous hits call the original virtual
method once for each affected player. Its original four stack arguments plus
the injected function/object arguments are consumed by the wrapper. The three
native continuation addresses match the replaced unconditional branches.

The first runtime fixture exposed a compiler-level ABI defect in the original
wrappers. With `/O2`, MSVC inlined the assembly adapter and retained the three
laser arguments in caller-volatile XMM0/XMM1/XMM2 across P1's native call. The
native trig routines overwrite those registers, so P2 received corrupted
arguments. The adapters now use explicit non-inlined C++ call boundaries;
the same rule isolates ECL/volley calls and the force-field position writer.
`audit-targeting-verify-compiled.py` checks the resulting COFF relocations for
both per-player call sites, in addition to verifying original-game signatures.

Native circle and laser return values differ during invulnerability: the circle
returns intersection `1` while skipping the miss, whereas the laser returns `0`
at `0x437c80..0x437c87`. Fixture 103 explicitly tests that distinction, then
removes P2's invulnerability and requires native P2 state 4 (the deathbomb
window, set by `0x438379`). Its geometric
query runs before that miss and records direct original P1/P2 query results.

The native graze routine `0x4391c0` increments the global graze count at
`0x4b0cdc` and shared point value. Keeping one native caller award per projectile
or laser interval implements the chosen team-once graze policy, even if both
actors graze simultaneously. The last collision actor controls position effects;
it does not duplicate the global award. Geometry-only queries do not replace the
last damage/graze actor.

Homing cleanup/selection has six passing version-specific signature comparisons;
the isolated force-field prefix has two. Homing selection preserves the native
single-player block when P2 is absent. Force-field selection preserves full
register/FPU/SSE state, uses the original x87 control word, rounds scaled X/Y
components to float before addition, and swaps the real player pointer only
around the native position writer.
That writer multiplies XY by **128**, converts to integers, then writes floats
back with **1/128** (double constants `0x4a3d48` and `0x4a3d40`). Fixture 105
requires both float and fixed coordinates to agree with this native scale.

The smoke-only `coop_audit_targeting.h` exposes `RunTargetingAudit(caseId,
details32)` without an external mapping. The caller runs it on the game thread
with two initialized real actors, records its result, and closes that owned game.
Zero result means all assertions passed; nonzero bits identify failed assertions.
`details32[0]` is the number of assertions evaluated. Cases are:

| Case | Assertions |
| --- | --- |
| 101 | Nearest/tie/dead fallback; frozen/nested ECL and volley scopes; actual installed angle hook; original owner remains unchanged |
| 102 | Full FPU/SSE image, flags and non-result GP registers across target loader and spawn selection; no-live native skip branch |
| 103 | Actual installed P2-only circle/laser hit and oriented query; collision owner; a separately recorded virtual-callee ABI for single/dual cancellation |
| 104 | Independent actor target-choice/lock behavior; both actors' 256 shot references cleared through destructor-load and installed native clear-helper routes; unrelated enemy references retained |
| 105 | Complete original force-field entry with controlled data; both actors move through native fixed-point writer and option refresh; neither eligible actor moves |

These fixtures intentionally mutate a fresh test game. Their presence and static
verification are not evidence of execution; coordinated build/run results are
recorded separately.

## Reproduction artifacts

- `audit-targeting-inventory.py`: full direct-operand scan and context extraction.
- `audit-targeting-references.json`, `audit-targeting-contexts.txt`: raw evidence.
- `audit-targeting-classify.py`, `audit-targeting-classified.json`,
  `audit-targeting-inventory.md`: classification of every direct reference.
- `audit-targeting-callers.py`, `audit-targeting-callers.json`: direct callers and
  address-taken references for helper/context boundaries.
- `audit-targeting-inspect.py`: bounded disassembly and direct-reference inspection.
- `audit-targeting-verify.py`, `audit-targeting-hook-bytes.json`: both retail
  profiles' patch signatures and item-call exemptions.
- `audit-targeting-verify-collision-homing.py`,
  `audit-targeting-collision-homing-bytes.json`: collision, target-lifetime and
  force-field patch signatures across both retail profiles.

No game launches or installed-file modifications were performed for this audit.
