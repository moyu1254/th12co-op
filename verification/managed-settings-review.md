# Managed input, settings and player-selection review

Date: 2026-10-30. Source working tree for the independent-character/local-settings implementation; release version, wire protocol and cooperative rules remain separate identifiers.

## Findings and corrections

- A native audio mode of `0` can coexist with a nonzero stored volume. The settings dialog previously displayed that stored volume and could enable audio when saving an unrelated display change. It now displays effective volume (`0` for disabled audio) and updates the audio fields only after an explicit volume change. An enabled BGM mode of `2` remains unchanged when adjusting its volume.
- The host and guest use the same production negotiation methods exercised by the tests. The guest submits its own character before the offer; the manifest binds both selections, session, seed, delay, rules and assets. The guest rejects an offer that changes its selected character. The host verifies the exact offer acknowledgement before sending the start confirmation.
- Local display/audio/input fields are omitted from the shared configuration digest. Simulation and unknown fields remain shared and are normalized for the online copy. Persisting an online session merges only local fields into the existing offline configuration. Score/clear records are neither imported into the online session nor saved back.
- Input configuration uses separate profiles for the online local player/offline P1 and offline P2. Validation and atomic replacement protect existing settings; a failed or malformed load does not silently overwrite the file. Only a foreground game receives physical keyboard/gamepad input. Keyboard and pad masks merge before online submission.

## Verification

Published the managed launcher to a separate directory while native/game tests used the normal distribution. No game, input automation, clipboard operation, audio endpoint change or native rebuild was performed for this review.

```powershell
& .\research\dotnet10\dotnet.exe publish .\src\launcher\TH12Coop.Launcher.csproj -c Release -r win-x64 --self-contained false -o .\build\managed-review --nologo
Copy-Item .\profiles\th12-online.cfg .\build\managed-review\th12-online.cfg -Force
& .\research\dotnet10\dotnet.exe .\build\managed-review\TH12Coop.dll --self-test
```

Result: publish exit `0`, no warnings/errors; self-test exit `0`, **18 test groups passed**, including **18 managed audit groups, 0 failures**. Detailed local output is in `build/managed-review/launcher-test-results.txt` and `build/managed-review/managed-audit-results.json` (not committed).

The added checks cover:

- Actual pinned loopback TLS negotiation for all 36 independent character pairs, with different local settings and compatible retail/Steam 1.00b identities.
- Legacy menu-selection agreement; rejection of mixed legacy/explicit selection, substituted guest selection, and altered offer acknowledgement.
- Every selection's native shared-memory publication contract.
- Shared/local digest separation, including shared hold-to-focus flags and unknown fields; local persistence preserving offline simulation options and original score bytes.
- Cancelled configuration saves and malformed session configuration preserving the prior file.
- Disabled audio with stored nonzero volume, separate BGM/effect changes, and preservation of enabled native BGM mode.
- Pure input interpretation for keyboard/pad merging, button mapping, axes, dead zones, inversion, POV directions, opposite-direction neutralization, disconnection and focus gating; input settings round trips and invalid-file preservation.

## Limits

This is a managed/static review, not a second-PC or WAN test. Actual controller button presses, reconnect behavior during play, and interactive settings-dialog layout/capture behavior were not exercised here. Earlier read-only enumeration confirmed one XInput slot and one WinMM slot could be read on the development PC; this does not establish physical-button or gameplay coverage. Native character behavior, real-game determinism and hidden/muted execution are recorded separately by the game-test owners. Subsequent quiet-test observation changes are outside this isolated managed test run.
