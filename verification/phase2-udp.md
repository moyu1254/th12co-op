# 部屋番号・Phase 2 の検証

2026-11-16、Windows 11（build 26200）、.NET SDK 10.0.403、runtime 10.0.14。通信 Protocol 6、協力 Rules 6、IPC 1。HTTPS・STUN・ICE・QUIC の試験は同一 PC で行った。

自己完結ビルド、native の診断ビルド、ランチャーの自己テスト23グループ（内部の managed 検証19グループを含む）、合流サーバーの自己テスト、UDP 結合試験、配布処理の合成試験6件が合格した。自己完結版は実ゲーム試験の後、TCP 送信期限の試験で受信バッファを待受ソケットに直接設定するよう修正して再ビルドした。

| 試験 | 実行内容 |
|---|---|
| 通常の番号接続 | ローカル Kestrel で発行・参加・候補交換。両者の照合コードを比較し、承認後に QUIC で往復。待機中止時は部屋を取り消し、ゲーム中の UDP 転送は接続の終了時に解放した。 |
| NAT によるポート変更と損失 | 模擬 NAT が両端の STUN Binding に異なる外側ポートを返した。両者ともそのポートを選定し、最初の QUIC Initial を1個破棄しても 4096 bytes のレコードと応答が届いた。 |
| IPv6 と IPv4 | 同じデュアルスタックソケットに両候補を渡した。両方に到達可能なときは IPv6、両側で IPv6 のチェックを破棄したときは IPv4 を選定し、実データが届いた。 |
| 認証と制限 | ICE パスワード不一致で経路が成立せず、取消で終了。QUIC は別の証明書・誤った参加鍵を拒否。ディレクトリは IP 証明、役割別トークン、単回の取得、失効、送信元別・全体の受付上限を検査。手入力 TCP/TLS の認証・レコードもランチャーの自己テスト対象。 |
| UPnP | 模擬 IGD に対して実際に確保した UDP ポートの有限リース、所有確認後の更新と削除、同一ポートの TCP 転送や他者が置き換えた転送の保持を確認。 |

## 対象環境

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

| 試験に使用したファイル | SHA-256 |
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
