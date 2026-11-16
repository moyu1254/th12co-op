# 部屋番号・Phase 2 の検証

2026-11-16、Windows 11（build 26200）、.NET SDK 10.0.403、runtime 10.0.14。通信 Protocol 6、協力 Rules 6、IPC 1。HTTPS・STUN・ICE・QUIC の試験は同一 PC で行った。

## 追加レビュー後の再検証（待機の再試行）

Windows 11 で合流サーバーの Release 自己テスト3グループが合格した。修正前の `899104e` では、送信元の受付上限に近い状態で待機枠不足の503または重複待機の即時204を受けると、その後の保存済み候補の取得が429になった。

修正版では、ホストの `offer` とゲストの `receive` それぞれについて、送信元とサーバー全体の受付枠を残り6回に設定して HTTP 処理を通した。即時204と503を各3回受けても残数を消費せず、両部屋が候補を取得できた。新規待機3件（中断1件を含む）、無効なトークン1件、不正な JSON 1件、保存済み候補の再取得1件で残り6回を使い切り、以後は429になった。ローカルの HTTPS 試験でも、待機枠が満杯の間に IP 証明と参加を受理し、待機中のホストに候補が届いた。

ランチャーの自己テスト23グループ（内部の managed 検証19グループを含む）と UDP 結合試験も合格した。通常版 1.00b の隔離コピー2つで60秒間接続し、両者ともステージに到達して **3,371フレーム**で一致した。通信エラー0件で両プロセスが終了した。ゲーム、ネイティブ DLL、検証用 DLL、ローダーの SHA-256 は下記の初回レビュー時と同一。実ゲーム試験は同一 PC の IPv4 ループバックで、別 PC・WAN・実ルーターは未検証。

| 今回使用したファイル | SHA-256 |
|---|---|
| TH12Coop.Rendezvous.dll | `216DDD20BA464F91CEE16B902A2E416C1912E9DAE45E36198294D4E1F5EED8FC` |
| TH12Coop.Phase1Tests.dll | `8E1D5795649D3E2EC3954F1FC74AC59D7643B739143E0894B5832ABB1E4B7CDE` |
| 自己完結版 TH12Coop.dll | `5EC94F743F4B19EFC9045E37EFA012DF28133106D0B06C9A58FD2416EDE58434` |
| 結合試験用 TH12Coop.dll | `90B4AEC962BA92BE09DE7C7F4CCC6D9C81E19D4672D498BAF116BDADAE70A1A9` |
| Keryx.Ice.dll（自己完結版・結合試験用で同一） | `5BFD6987606B103B48D92FE51E5432737FC05CA12BC996E3FD04CBB4C166311E` |

## 初回レビュー後の再検証（`899104e`）

自己完結版ランチャーの23グループ（内部の managed 検証19グループを含む）、合流サーバーと UDP 結合試験がすべて合格した。手入力 TCP/TLS の接続・認証失敗・レコード入出力も検査した。配布処理の合成テストは6件合格した。

通常版 1.00b の隔離コピー2つを番号方式で接続し、60秒間で両者とも **3,359フレーム**を確認した。両者がステージに到達し、1P・2P の自機弾をそれぞれ最大24発確認した。無音・非表示の検査に合格し、通信エラーは0件、両ゲームプロセスの終了も確認した。実ゲームの範囲は同一 PC の IPv4 ループバックで、別 PC・WAN・実ルーターの UPnP は未検証。

| 試験 | 判定条件 |
|---|---|
| 合流通知の再取得 | 通知を受け取る前に HTTP 要求を中断し、参加後に同じ所有者トークンで同じ候補を再取得する。応答の再取得にはゲストトークンが必要。同じ応答の再登録を受け付け、内容の差し替え・二人目の参加・期限切れ後の取得を拒否する。 |
| 待機中の受付 | テスト用の待機枠を1件に制限して満杯にし、追加ポーリングが503で終わる間も IP 証明と参加が成功することを実際の HTTPS 接続で検査する。本番設定は64接続中48件が待機の上限。 |
| ホストの準備待ち | ホストがゲストの参加通知を受けてから46秒待つ。修正前の `1e680b7` ではゲストの45秒期限で失敗した。修正版では同じ条件で ICE・QUIC 認証後に双方向のレコードが届く。 |
| ICE の送信元 | 候補交換の前後で、相手の申告に含まれないループバック IP からのチェックが候補・ペアを消費しないことを検査する。確認済み IP のポート変更で接続し、学習枠が埋まった後も選定済みのペアを維持する。同じ IP の以前のポートから届くデータは QUIC の手前で除外する。 |
| QUIC の維持・終了 | 47秒間アプリ通信を止め、終了レコード `0x14` の完全な受信と相手側の終了コード0を検査する。別の接続で読み取り待ち中に ICE 経路を閉じ、3秒以内に経路の例外が呼び出し側に届くことを検査する。 |

| レビュー修正後の試験ファイル | SHA-256 |
|---|---|
| 通常版 1.00b th12.exe（両コピー同一） | `D8D644D2E64957A3031B1A1399D0502E1DDAA5252D2C4E492770AD6717827628` |
| 自己完結版 TH12Coop.dll | `AB157C9C96304911C5476089A70606162EFC131E6CAADFE205F894CA7B537152` |
| 結合試験用 TH12Coop.dll | `B33DAB2FED33AE049D1800703BC0C1EC6C9DDECB0E4582B979F2594110D2A165` |
| TH12Coop.Phase1Tests.dll | `64CFF7B38B669057ECA15F208DC817E7E1D3C5C176AC429564E148CA691D9F09` |
| TH12Coop.Rendezvous.dll | `7E4764EB7C8E0BA137156B93F25C387CE28B260707DAD1C107A96A49AE148801` |
| Keryx.Ice.dll（自己完結版・結合試験用で同一） | `2EB4D9A12CB2EE2665A1F6A38F20E92A19A59C5EBD889C55186F80C7D57E2B73` |
| th12coop-native.dll | `B2636F3FA89FBE40CD1D38C0A86543065B4C3675FDB9ADE6FE7BE1D76E425757` |
| injector.exe | `797D09C9B290AE74B3DB8346C5D64FDFFDD41982CB4CD336619234AA3B4C7A0E` |
| th12coop-quiet-test.dll（検証専用） | `876133272885F12EB22C7A55BAC7AD46D44941A78DB7E185215DB3DBB5C40DB0` |

## 初回実装（`1e680b7`）

自己完結ビルド、native の診断ビルド、ランチャーの自己テスト23グループ（内部の managed 検証19グループを含む）、合流サーバーの自己テスト、UDP 結合試験、配布処理の合成試験6件が合格した。自己完結版は実ゲーム試験の後、TCP 送信期限の試験で受信バッファを待受ソケットに直接設定するよう修正して再ビルドした。

| 試験 | 実行内容 |
|---|---|
| 通常の番号接続 | ローカル Kestrel で発行・参加・候補交換。両者の照合コードを比較し、承認後に QUIC で往復。待機中止時は部屋を取り消し、ゲーム中の UDP 転送は接続の終了時に解放した。 |
| NAT によるポート変更と損失 | 模擬 NAT が両端の STUN Binding に異なる外側ポートを返した。両者ともそのポートを選定し、最初の QUIC Initial を1個破棄しても 4096 bytes のレコードと応答が届いた。 |
| IPv6 と IPv4 | 同じデュアルスタックソケットに両候補を渡した。両方に到達可能なときは IPv6、両側で IPv6 のチェックを破棄したときは IPv4 を選定し、実データが届いた。 |
| 認証と制限 | ICE パスワード不一致で経路が成立せず、取消で終了。QUIC は別の証明書・誤った参加鍵を拒否。ディレクトリは IP 証明、役割別トークン、単回の取得、失効、送信元別・全体の受付上限を検査。手入力 TCP/TLS の認証・レコードもランチャーの自己テスト対象。 |
| UPnP | 模擬 IGD に対して実際に確保した UDP ポートの有限リース、所有確認後の更新と削除、同一ポートの TCP 転送や他者が置き換えた転送の保持を確認。 |

### 対象環境

| 接続環境 | 結果 |
|---|---|
| 同一 PC、通常版 1.00b の隔離コピー2つ、IPv4 ループバック | 結合試験用ビルドで実ゲーム2プロセスを番号方式で接続し、60秒で両者とも3,379フレームを確認。両者がステージに到達し、1P・2P の自機弾をそれぞれ最大26発・24発観測した。無音・非表示の検査を通り、通信エラー0件で両プロセスが終了した。 |
| 同一 PC、模擬 NAT・IPv4/IPv6・模擬 IGD | 上表の条件で確認。 |
| 別 PC・同一 LAN、実ルーターの UPnP | 未検証。 |
| WAN、フルコーン／制限／ポート制限／シンメトリック NAT の各組み合わせ × IPv6 有無 × UPnP 可否 | 全組み合わせ未検証。UDP が遮断される場合や宛先依存の NAT などで直接経路が成立しない場合は接続できない。中継は Phase 3。 |

今回の実ゲーム試験は通常版 1.00b に限る。通常版 1.00a・Steam 版、全ステージ、通話を使った人間による照合は未検証。

## 再現手順

```powershell
.\build.ps1 -SelfContained
.\build.ps1 -NativeOnly -SmokeTest
.\scripts\Test-Repository.ps1 -RunSelfTest
dotnet run --project src/rendezvous -c Release -- --self-test
dotnet run --project src/phase1-tests -c Release
py scripts/test_package.py

dotnet publish src/phase1-tests -c Release -o build
Copy-Item dist/th12coop-native.dll,dist/th12coop-quiet-test.dll,dist/injector.exe,dist/th12-online.cfg build/
dotnet build/TH12Coop.Phase1Tests.dll --games game-test-retail-1.00b-net-host game-test-retail-1.00b-net-guest
```

| 初回実装の試験に使用したファイル | SHA-256 |
|---|---|
| 通常版 1.00b th12.exe（両コピー同一） | `D8D644D2E64957A3031B1A1399D0502E1DDAA5252D2C4E492770AD6717827628` |
| 自己完結版 TH12Coop.dll | `C6837DC3BAD34D333AD1D33E131B54733541431D38872AD1F0CB21864E5006D7` |
| 結合試験用 TH12Coop.dll | `DCA79B017C80990DCD8D8A9A76A6668CE7F2DAE6DB6DCD170536ECCD7E494966` |
| TH12Coop.Phase1Tests.dll | `C880575446349F84EB39259C934A15BA37214EC3C2BF4CDF9ACD141FEE7D4AF6` |
| TH12Coop.Rendezvous.dll | `18E5EA55D7A4D4D3A47DF5C19F650BF4AE46B05A7168AEB36131BB41709B366C` |
| Keryx.Ice.dll | `323C0B6A15EE8EDBCBC61CA74526A54D195B27F9114C31EE9EE3CFB59B96EE6B` |
| th12coop-native.dll | `E6D9BE8C93542AEB8BD6180A97BBF01FF1FF12464E382AEA580DFF37B8701F16` |
| injector.exe | `EEE71E84EF0268588B908689BF1B468486BF5011559D61789D46B41C491892CE` |
| th12coop-quiet-test.dll（検証専用） | `8C13F949122F5CFC9072B1EE6B5DD82EBCE73B1859EAD6EF212213E4D7B00497` |
