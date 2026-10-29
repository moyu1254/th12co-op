# Item and UFO re-audit

This audit rechecks the item/progression implementation against the original retail executables, rather than treating the earlier audit as proof. It adds destructive smoke fixtures 9–19 in `src/native/coop_audit_items.h`. The collection and payout probes passed, but a further probe reproduced missing future state in the online fingerprint. The resulting production change is in `lockstep.h`; the rules in `coop_items.h` and `coop_progression.h` remain intact. The existing [cooperative rules](cooperative-rules.md), including promotion from local attraction to full auto-collection when either actor crosses the collection line, remain intentional.

The final build-specific execution record is [verification/RESULTS.md](../verification/RESULTS.md). The results below describe the initial re-audit runs, not every subsequent build or every game mode.

## Original executable evidence

| Profile | SHA-256 |
|---|---|
| Retail 1.00a | `e2939ba3f92e427216a619191a682a8197cc59a64eab99302a7b98811d6120a9` |
| Retail 1.00b | `d8d644d2e64957a3031b1a1399d0502e1ddaa5252d2c4e492770ad6717827628` |

The local read-only script `research/audit-items-recheck.py` compared 609 instructions in seven focused regions. Instructions and operands matched after allowing the verified +0x20 shift of code references. It also checked the constants used below independently in both executables. Its report is `research/audit-items-recheck.json`. This is a scoped comparison, not an assertion that arbitrary game addresses can be translated by +0x20; code before the score-format insertion does not follow that rule.

Addresses in the following table are retail 1.00b.

| Path | Evidence and conclusion |
|---|---|
| Changing token proximity | `0x42628F..0x426375` subtracts the selected actor's actual X/Y from the token position and compares squared distance with 3600. Nearness freezes both lifetime and color timers. It does not measure distance from the stage origin. |
| Token pickup | Color processing ultimately reaches the separate common pickup path at `0x4260AF`. Color freeze does not itself consume the token. The cooperative selector must supply a valid actor for both paths independently. |
| Local homing | `0x425FFF..0x4260AF` uses the current selected actor for motion. States 3, 4 and 8 retain their assigned cooperative owner while that actor remains eligible. The three angle-helper exemptions match the actual item call return addresses. |
| Deathbomb-window release | `0x426092..0x4260AF` explicitly changes attraction to state 1 when the native target is in player state 4. The state-3 and state-8 paths reach the same release. Full point value is not a permanent native entitlement after attraction has been released. |
| Physical point award | `0x426B10` reads the actual collector's Y; `0x426B31` grants full value to state 3. The documented cooperative state4→3 promotion therefore changes speed/value deliberately; it is not classified as a regression in this audit. |
| UFO absorption | `0x4271C0` can convert eligible food to state 7 after the UFO's initial 60 ticks. `0x426978` consumes the physical item and calls the food accounting routine once. Absorption near the UFO precedes the common player pickup test. |
| UFO food accounting | `0x44ACA0` updates one shared pair of food counters. Power and point items increase fullness by `0.03200000151991844 - stage * 0.0020000000949949026`. Rainbow swaps the two food counters. The formulas and constants are identical in the two retail builds. |
| Fullness bonus | The food routine clamps fullness to 1 and emits the color-specific bonus on crossing the threshold. Additional food still increases food totals but does not reissue that bonus. Red emits a life fragment, green a bomb, blue no additional full-bonus item; rainbow emits a changing token based on the last collected color. |
| UFO death payout | `0x44AF00` generates one large reward carrying food counts/color/multiplier, plus the native color-specific drops. The item payload at `+9C8..+9D4` remains separate from its attraction state. |
| Shared power | Small-power accounting at `0x426FF0` retains the native cap and max-power score conversion. Threshold changes call the existing paired option rebuild; the award itself is not called twice. This arithmetic was inspected, while the new runtime fixtures below specifically check ordinary and large-reward pickup. |

The UFO spawn, tick, feed and payout paths contain no direct Player-singleton references. Their food totals and rewards belong to the one world/UFO. Two ordinary player shot contributions can cause the one UFO enemy to die earlier; this alone does not require doubling food or payout. The fixtures below do not establish every indirect enemy-death scheduling path or all timing combinations of two-player damage.

## Native fixtures and observed baseline

Each case requires a newly started disposable game with both players active, no existing UFO, no dialogue and no transition in progress. The fixture runs on the game thread before P1's normal tick. It clears the native item pool, changes the stated positions/states/resources, invokes the original spawn/update functions, and then the harness closes that game. These are not input-only gameplay tests and must never be invoked against an ordinary play session.

| Family 3 case | Actual native behavior checked | Failure bits |
|---|---|---|
| 9 | P2 alone freezes a changing token's timers at distance 50, then physically collects it; the shared token inventory increases once. | 1 freeze/timers; 2 pickup/duplicate award |
| 10 | A token near the stage origin with both players far away does not freeze. An ineligible nearby P1 does not freeze/collect it. Two overlapping active actors collect one blue token. | 1 origin branch; 2 ineligible actor; 4 shared pickup |
| 11 | Native states 3/4/8 retain owner P1 after P2 becomes closer, transfer to P2 when P1 enters state 4, and move in the correct direction. Reusing the state-8 pool slot starts fresh ownership. | 1/2 state3; 4/8 state4; 16/32 state8; 64 reused slot |
| 12 | Both actors becoming ineligible releases state8→state1 while preserving the large reward's color/food/multiplier payload. Revived P2 receives power 3 and the score award exactly once. | 1 release/payload; 2 award |
| 13 | A real native UFO absorbs one power and one point item, with stage arithmetic set to 1, 6 and 7. The contexts include distant actors, both overlapping the UFO, and both ineligible. Repeating the manager update does not add food again. | 1/4/16 initial absorption; 2/8/32 repeat |
| 14–17 | Real native red/green/blue/rainbow UFOs cross fullness .99→1 via an absorbed item, then absorb another item. Only the native first full-bonus is generated. | 1 first food/fullness; 2 first bonus; 4 extra food; 8 duplicate bonus |
| 18 | After actual absorption of three power and two point items, the original death-payout routine is called once. It produces one large reward with those counts, one red life-fragment drop and one red token. P2 collects the large reward once. | 1 payout/payload; 2 award; 0x100 missing large reward |

Initial retail 1.00b execution passed all ten cases with zero failure masks:

- Cases 9–12: `build/audit-items-before/retail-1.00b/`, smoke SHA-256 `4b2d59dcc3de5b2dfde7c8f20a14e622de9478701bc7bed7b758e42f04763d5e`.
- Cases 13–18: `build/audit-ufo-before/retail-1.00b/`, smoke SHA-256 `5609ea1f9c841319fdfee073142915e966da1ff7b4356cb602b2be5d3bb09a0f`.

Cases 12 and 18 both observed power 3 and score 5000 unchanged on the following item update. Case 13 observed one of each food and fullness approximately .060, .040 and .036 for the two absorbed items at stage values 1, 6 and 7. The case changes the stage arithmetic input in a stage-1 fixture; it does not claim to play stages 6 or Extra. Case 18 calls the payout routine directly, so it validates payout/collection but not the complete enemy HP/death scheduler.

The local baseline results above do not establish runtime coverage on retail 1.00a or Steam. Cross-version execution results, if performed, belong in the final verification record. The seven-region static comparison independently supports the addresses and arithmetic used by these fixtures.

## Lifetime, input context and synchronization

The owner sidecar contains slot-indexed actor IDs, not heap pointers. Native spawn resets the item timer; the first pass of a reused slot discards prior ownership. Actor activation/destruction and manager replacement reset the sidecar. The native item pass restores the Player singleton and all five input words after its single update; per-actor focus comes from the controls already consumed in that frame.

`DeterministicStateHash()` covers the owner array. Pending-item variables and the temporary selected singleton exist only inside the manager update and are cleared/restored before the frame-boundary fingerprint. Native item state, position, velocity, timers and reward metadata require the separate native item fingerprint; owner hashing alone is not a complete item digest.

### Reproduced fingerprint omission

Case 19 constructs a native UFO, absorbs real food, and invokes its original payout routine. It then changes and restores one native scalar at a time while calling the **actual complete frame fingerprint**, not a replacement model or only the owner-sidecar hash. The offline fixture supplies temporary fingerprint-output storage without enabling lockstep or sending input. Each of the 20 mutations must alter the digest; restoring the original value must restore the original digest.

The original digest failed all 20 probes: mask `000fffff`, with no restoration-error bit. The retail 1.00b record is `build/audit-item-sync-before/retail-1.00b/family-3-case-19-shot-0.json`, smoke SHA-256 `6952ed9a25deb3c15bed6b3eebbdccdd50ad5cd4b59d508ca506834e4339c561`. This is an observed synchronization-detection defect even though the ordinary collection/payout tests passed: equal current position and score did not establish equal future motion, token color or reward.

| Failure bits | Previously omitted state | Initialization and use |
|---|---|---|
| 0 | Reward item `+984` speed | Native reward spawn zeroes it at `0x4276F9`; state8 reads/accelerates it at `0x426192..0x42621E`. |
| 1–3 | Changing-token timer `+99C/+9A0/+9A4` | Native token spawn initializes all three words at `0x427571..0x42757D`. Color changes consume this timer. The speed pointer at `+9A8` is excluded. |
| 4–7 | Large-reward `+9C8/+9CC/+9D0/+9D4` | Native payout writes color, power-food count, point-food count and multiplier. Native pickup reads them to determine the later award. |
| 8–10 | UFO timer `+10/+14/+18` | Constructor/initialization establishes timer values; suction readiness and UFO lifetime consume them. The speed pointer at `+1C` is excluded. |
| 11–13 | UFO duration `+28`, active `+2C`, color `+30` | These select lifetime, suction and payout behavior. |
| 14–16 | UFO power-food `+48`, point-food `+4C`, fullness `+50` | These determine full-bonus and death reward before the player collects the resulting items. |
| 17–19 | Globals `4B0C5C/4B0C60/4B0C64` | Native `0x44A4B2..0x44A4C8` uses them to decide whether and which UFO to spawn. |

The corrected digest hashes the second timer only for native token types 10–15 and the reward speed/payload only for reward types 16–18. This avoids treating unrelated stale item storage as meaningful state. The UFO constructor at `0x449F80` zeroes its full 0x6C-byte allocation; the added fixed scalar ranges are initialized. The correction also includes UFO EnemyID `+38` and, while active, normalizes its enemy association to the bounded live enemy-list index. It does not hash the raw enemy pointer, timer pointer, registry node, ANM IDs or popup presentation fields. A non-null enemy association outside the live list has a separate sentinel from null and is not dereferenced.

After-change fixture and lockstep results must be read from [verification/RESULTS.md](../verification/RESULTS.md); the reproduced failure and static review alone are not a runtime pass for the correction.

This re-audit did not add GUI callback hooks or change stage transitions. The actor-local transition helper was included in the two-executable comparison. Existing HUD callouts precede the native floating-point calculations and select local pointers rather than changing global targeting. This static review does not replace lifecycle, pause/retry, campaign or full synchronization tests recorded elsewhere.

The attempted collection edge cases passed, the large-reward payload was retained, and the native release behavior explained the suspected collection-state loss. The separate fingerprint omission was reproduced before correction. No collection-speed, food-count, reward, enemy-HP or damage-balance adjustment was made in response to that synchronization defect.
