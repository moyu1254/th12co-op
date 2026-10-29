# Projectile digest audit

This is a bounded source/disassembly audit of state used by the existing deterministic input simulation. It does not establish that every future game state is covered by the synchronization fingerprint. Executed fixture results and release identities belong in the verification report.

Addresses below refer to the inspected retail 1.00b executable. The verified native code mapping uses an address lower by `0x20` for retail 1.00a; object offsets remain the same. Steam 1.00b uses the same decrypted gameplay code. The audit used the installed executable and the existing local reverse-engineering maps; no executable bytes or game assets are included here.

## Reproduced branch to exercise

The original bullet manager zeroes its complete `0x4DEBE0` allocation at `0x40984F–0x409857`. Each bullet occupies `0x9F8` bytes. Consequently the explicit numeric Ex fields below have initialized storage. This does not make pointer fields suitable for cross-process hashing.

`ExAccel` at `0x40B6F0` takes the bullet in EAX. It compares timer current `+0x738` against duration `+0x75C` at `0x40B6FC–0x40B702`. Expiry clears flag `4` at `+0x528` and returns 1. Otherwise it adds the scalar/vector acceleration to speed and velocity, advances the timer at `+0x734`, and returns 0. The previously hashed position, velocity, speed, angle, Ex flags and Ex definitions can therefore be identical while the next native movement decision differs.

Smoke family 5 case 5 allocates two private zeroed pools and calls this original native function. One timer is current 0, the other current 1, both with duration 1. It checks the actual branch, resulting speed/velocity, active flag, timer and the production `Fingerprint` before and after execution. The custom EAX ABI uses a non-inlined adapter. It does not tick or free any real bullet.

## Explicit Ex state allowlist

Each timer range below is exactly three 32-bit numeric values: previous integer, current integer and fractional float. The following speed pointer at timer `+0x0C` is excluded. Timer control storage and unused record padding are not included. `coop_projectile_digest::Append` adds only the fields for currently active Ex flags, within each live bullet slot already bounded by the fingerprint loop.

| Active flag | Timer start | Additional future-state scalar offsets |
|---|---|---|
| `0x1` | `0x700` | — |
| `0x4` | `0x734` | `748, 750, 754, 758, 75C` |
| `0x8` | `0x768` | `77C, 780, 790` |
| any of `0x70` | `0x79C` | `7B0, 7B4, 7C4, 7C8, 7CC` |
| `0x100` | none: bounce timer is unused | `7E4, 7F8, 7FC, 800` |
| `0x1000` | `0x804` | — |
| `0x20000` | `0x838` | — |
| `0x40000` | `0x86C` | — |
| `0x800000` | `0x8A0` | `8B4, 8B8, 8C8` |
| `0x2000000` | `0x8D4` | `8E8, 8F0, 8F4, 8F8, 8FC`, interpolation described below |
| `0x8000000` | `0x908` | `924, 928, 92C, 930` |
| `0x20000000` | `0x93C` | `950, 958, 95C, 960, 964` |
| `0x400` | `0x970` | `998` |

The steering interpolation at `+0x9A8` contains four float3 values through `+0x9D4`, then its numeric timer triple at `+0x9D8..0x9E0`. Include end time `+0x9EC` and method `+0x9F0`; exclude speed pointer `+0x9E4` and control `+0x9E8`. Native Ex setup writes the vector operands at `0x40B30B–0x40B38F`, and the steering update reaches the interpolator at `0x40C2CF`.

The Ex dispatcher at `0x409CA1–0x409DCF` proves the flag-to-update mapping. The two later Ex updates are called at `0x409F85` and `0x409F98`. Setup and update instructions in `0x40AA60–0x40C4D0` prove the listed operand types and consumption. Setup-only values with no later read in these routines are omitted, as are unused gaps between named operands. In particular, `+0x74C` is the direction angle written at `0x40AB9C` and immediately read at `0x40ABBD` to construct the acceleration vector. The later acceleration update uses the resulting vector, not this setup-only angle; it is intentionally excluded.

Smoke case 6 probes each of the 36 timer words, 35 operand words and 17 interpolation words individually, checks restoration after each change, and checks that changing all 13 associated timer-speed pointer addresses does not change the digest. A separately allocated equivalent pool/laser fixture checks allocation-address independence. These are sensitivity and exclusion tests; they are not a campaign traversal.

## Laser instruction position

The laser base constructor zeroes `0x454` bytes at `0x427EF0–0x427EF8`, including the Ex instruction index `+0x42C`. The line-laser instruction interpreter reads it at `0x428B12` and `0x428B20`, then advances it at `0x428B5F`. It selects the next definition from the native Ex instruction array; active flags alone do not identify that position. Case 6 also checks sensitivity to changing this index from 0 to 1 in a private line-laser fixture with the known native vtable identity.

## Recorded baseline

The local evidence is in `build/audit-bullet-input-before/retail-1.00b/family-5-case-{5,6,7}-shot-0.json`. It was produced with Smoke SHA-256 `04577443a2a9c1ae85e4ebf822e8e86e7554640f24b8fb582f843bda8ee52510` before the focused digest/input fixes.

| Case | Recorded failure mask | What the result establishes |
|---|---|---|
| 5 | `0x1` | Only the pre-step fingerprint distinction failed. The real native routine returned 0 versus 1, changed speed to 1.25 versus 1.0, kept versus cleared the acceleration flag, and produced a distinguishable post-step state. |
| 6 | `0xF` | All 89 tested scalar mutations were missed: 36 timer words, 35 operands, 17 interpolation words and the laser index. All 13 pointer-exclusion checks, restoration checks and allocation-address independence passed. |
| 7 | `0x2` | The actual input barrier exported the raw `0xFFFFFFFF` fixture mask instead of the protocol mask. The valid committed input was accepted; this isolates the producer conversion issue. |

These are expected failing baseline results, not a claim that the corrected build has passed. Corrected-build evidence must identify its own binary and completed run.

## Coverage limits retained explicitly

This focused change does not claim whole-engine state completeness. Remaining areas that need their own normalization, initialized-field review and native behavioral fixtures include:

- Bullet lifecycle/collision flags at `+0`, age/graze timers at `+0x4E4/+0x4F8`, expiry and other control scalars outside the Ex runtime records. For example, `0x4099E9` reads deletion flag 8, `0x409B1E` gates appearance collision on age, and `0x409C71` checks the prior-graze flag. Their omission can delay detection until a currently hashed consequence changes.
- The bullet manager allocation cursor, a process-local pointer that must be converted to a validated pool index if added. It cannot be hashed as an address.
- Laser active Ex runtime records, instruction definitions and per-kind inner state beyond the added common instruction index. Constructors initialize the common base; per-kind constructors/copy paths require a type-specific field audit before extending coverage.
- Player homing associations in the actor and individual shots. These are native enemy pointers and require a bounded, stable live-enemy identity rather than raw address hashing. The current homing module state hash does not encode these associations.
- Further player firing/geometry timers and some enemy interpolation state. Whole-object hashing would include pointers or fields without sufficiently verified initialization and is not a valid substitute for that audit.

## Raw input versus protocol input

The native controller merge at `0x462B74–0x462B92` can add bit `0x200` from a configured controller button; the returned mask reaches the input hook at `0x463202`. This is outside the cooperative protocol mask `0x1FB`. `Poll` now converts the raw device word at the native IPC producer boundary, before publishing its frame. The managed IPC/network guard continues rejecting unsupported bits in data claiming to already use the cooperative protocol. Offline input still returns through the existing inactive-network path.
