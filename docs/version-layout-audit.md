# TH12 executable layout audit

Audit date: 2026-10-29. Addresses below are virtual addresses for the original
32-bit image loaded at `0x400000`. Installed originals were read without changes.

## Verified inputs

| Distribution | SHA-256 | File bytes | Entry RVA | Image bytes |
| --- | --- | ---: | --- | --- |
| Retail 1.00a | `e2939ba3f92e427216a619191a682a8197cc59a64eab99302a7b98811d6120a9` | 737280 | `0x6e7fd` | `0xd9000` |
| Retail 1.00b | `d8d644d2e64957a3031b1a1399d0502e1ddaa5252d2c4e492770ad6717827628` | 737280 | `0x6e81d` | `0xd9000` |
| Steam 1.00b | `eb275a8c285fd8450fa5ac79fa730a759aeca8b2904aeb16d30e65859372a459` | 878736 | `0xd9310` | `0xfc000` |

The launcher/injector checks the complete file hash. The native module additionally
checks image base, PE machine, section layout, entry point, image size, `.text`
virtual size and the version string at `0x4a1314`. These profiles describe the
three inspected executables; they do not authorize an arbitrary patched executable
or a future build merely because it reports the same version.

## Retail differences

The score-format function at `0x401610` grows by `0x20` bytes in 1.00b. The complete
1.00a range `[0x401720, 0x497ab5)` was compared to the corresponding 1.00b range
at `+0x20`. After normalizing 1,840 code references, the only remaining byte is
the startup version character at 1.00a `0x42f4b9` (`a` becomes `b`). Every code
address used by the native MOD falls within that verified shifted range.

The `.rdata` range `[0x498000, 0x4ad000)` has 396 code pointers that move by
`0x20`, 54 SafeSEH RVAs that move by `0x20`, and three version-label characters.
There are no other differences. The `.data` range `[0x4ad000, 0x4d6460)` has 155
shifted code pointers and no other differences. Global addresses, imports, vtable
locations and the object offsets used by the MOD therefore remain unchanged;
code targets and embedded SEH handler addresses are translated by the profile.

Both executables pass all 17 complete-instruction hook signatures before the
native module applies game hooks. The two constructor/destructor signatures
include translated SEH handler immediates. Callback stubs containing relative
instructions are replaced directly instead of copied into trampolines.

| Purpose | Retail 1.00a | Retail/Steam 1.00b |
| --- | --- | --- |
| Bomb callback / implementation | `0x406b90` / `0x406cc0` | `0x406bb0` / `0x406ce0` |
| Item loop / continuation | `0x425c27` / `0x425c2d` | `0x425c47` / `0x425c4d` |
| Item callback | `0x427360` | `0x427380` |
| Initial RNG seed call / continuation | `0x42f562` / `0x42f568` | `0x42f582` / `0x42f588` |
| Stage load | `0x4304e0` | `0x430500` |
| Player activate | `0x4360e0` | `0x436100` |
| Player destroy / create | `0x436250` / `0x4363f0` | `0x436270` / `0x436410` |
| Player tick / draw implementations | `0x436b80` / `0x4375e0` | `0x436ba0` / `0x437600` |
| Tick / draw callbacks | `0x437640` / `0x437650` | `0x437660` / `0x437670` |
| Rectangle / circle / laser collision | `0x4377f0` / `0x437960` / `0x437a60` | `0x437810` / `0x437980` / `0x437a80` |
| Option update / damage | `0x438590` / `0x439eb0` | `0x4385b0` / `0x439ed0` |
| Replay save | `0x43bbf0` | `0x43bc10` |
| Audio / ANM dispatch | `0x453f80` / `0x4603b0` | `0x453fa0` / `0x4603d0` |
| Callback registry remove | `0x462870` | `0x462890` |
| Input poll / continuation | `0x4631e2` / `0x4631e8` | `0x463202` / `0x463208` |
| Native free | `0x46ca2f` | `0x46ca4f` |
| Destroy / create SEH handlers | `0x497099` / `0x49771b` | `0x4970b9` / `0x49773b` |

## Steam startup

Steam's on-disk `.text` is wrapped. An isolated launch through the installed
Steam client, with its normal ownership/startup checks, yielded runtime `.text`
identical to retail 1.00b. Steam `.rdata` and `.data` are also byte-identical to
retail 1.00b in the static comparison. The MOD does not distribute an extracted
executable or alter Steam authentication.

For an early online launch, the injector parks the known Steam entry point and
loads the native module. The module arms a temporary `timeGetTime` import hook
at IAT `0x498298` and reports status **5** (armed). All calls except the game's
initial seed call are forwarded to the original import. At return address
`0x42f588`, the callback requires the primary thread ID supplied at network
header offset **92**, restores the original import, verifies all 17 plaintext
game signatures, and installs the hooks synchronously. It then returns the agreed
initial seed. This ensures installation occurs before the first RNG seed store,
without a polling race. A mismatch stops this owned game process for launcher
cleanup (error 15: thread/reentry; 16: import restoration; 17: game-hook install).

Status 5 is not playable readiness. `Ready()` and the device-ready worker run
only after successful installation. Ordinary offline injection at the title
screen already sees plaintext code and installs directly.

## Reproducible evidence

Read-only local scripts and generated reports in `research/`:

- `prove_layout.py` → `retail-layout-proof.json`: 1,840 normalized references,
  one expected version character in the complete shifted code range.
- `prove_version_data.py` → `version-data-proof.json`: no unexplained `.rdata`
  or `.data` bytes after the documented normalizations; Steam equals retail 1.00b.
- `check_version_hooks.py` → `version-hook-signatures.json`: 17 signatures pass
  for each retail executable (34 comparisons).
- `test_steam_bootstrap.py`: verifies the encrypted text remains unchanged while
  armed, then checks the first input barrier is reached on the primary thread.

The static checks above passed after the native profile changes. Runtime results
are recorded separately by the coordinated build/game verification; static
layout equivalence alone is not a gameplay or network test.
