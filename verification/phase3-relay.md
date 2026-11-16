# Phase 3：部屋番号の中継フォールバック

2026-11-16。通信 Protocol 7、協力 Rules 6、ローカル IPC 1。Windows 上の同一 PC にテスト用の HTTPS/WSS サーバーを起動し、二つのランチャーの通信処理を実行した。外部へのデプロイは行っていない。

| 条件 | 確認内容 |
|---|---|
| 直接 UDP が使える | ICE・QUIC で接続して照合後にレコードを往復。両側とも WSS 要求は0件。 |
| 片側の候補がない | ホストの IP 証明を取得できない条件で、同じ番号から双方が WSS に1回ずつ接続。ゲスト側だけの IP 証明が失敗する場合は下記の実ゲームで検査。 |
| 候補はあるが到達不能 | テスト用ディレクトリの応答で両側のポートを応答しないループバックソケットに置き換えた。実際の ICE チェックの後に WSS で照合し、4096 bytes のレコードと応答を交換。 |
| ICE は成功するが QUIC が通らない | 模擬 NAT の選定済み経路で QUIC Initial を破棄。ホスト・ゲストの両方が中継の対象となる到達性エラーを返すことを確認。 |
| 参加認証・終了 | 直接 QUIC と中継内 TLS で、証明書や参加鍵が違えば検証する側で認証失敗。WSS で両者の照合コードが一致し、片側だけの承認では開始しない。送信直後に閉じても最後のレコードが届き、その後にストリームの終了を検出。 |
| 接続制限と解放 | 応答登録前・役割違い・二重接続・使い直しを拒否。実際の WSS ソケットで、断片を合算して16 KiBちょうどは通り、超過時はその一部も相手に渡らないことを確認。小さいペア数・転送量・期限を設定し、枠不足、双方向合算の上限、片側待ちと稼働中の失効、切断後の枠の再利用を検査。 |

## 実ゲームと未検証範囲

通常版 1.00b の専用コピー2つを、片側の IP 証明だけが取得できない条件で接続した。ホストとゲストの中継要求は各1回。60秒で両者ともステージに到達し、3,362フレームまで一致した。1P・2P の自機弾を観測し、通信エラーは0件。両プロセスは非表示・無音の検査を通り、終了を確認した。

| 接続環境 | 判定 |
|---|---|
| 同一 PC、IPv4 ループバック、通常版 1.00b 同士 | WSS 中継で接続可。 |
| 別 PC・同一 LAN、実機の UPnP | 未検証。 |
| 別 PC・別回線：フルコーン／制限／ポート制限／シンメトリック NAT の各組み合わせ × IPv6 有無 × UPnP 可否 | すべて未検証。模擬環境での直接接続失敗や中継成功から、個別の WAN 環境の成否は判定していない。 |

通常版 1.00a・Steam 版での中継、全ステージ、通話での人手照合、公開サービスの運用・負荷・費用は今回の検証外。

Windows 11 Home（build 26200）、.NET SDK 10.0.403。自己完結版のビルド、ランチャー自己テスト23グループ（内部の managed 回帰19グループを含む）、ネイティブ診断ビルド、配布処理の合成テスト6件も合格した。

## 再現手順

```powershell
.\build.ps1 -SelfContained
.\build.ps1 -NativeOnly -SmokeTest
.\scripts\Test-Repository.ps1 -RunSelfTest
dotnet run --project src/rendezvous -c Release -- --self-test
dotnet run --project src/phase1-tests -c Release
py scripts/test_package.py

dotnet publish src/phase1-tests -c Release --artifacts-path build/phase3-obj -o build
Copy-Item -LiteralPath dist/th12coop-native.dll,dist/th12coop-quiet-test.dll,dist/injector.exe,dist/th12-online.cfg -Destination build
dotnet build/TH12Coop.Phase1Tests.dll --relay-games game-test-retail-1.00b-net-host game-test-retail-1.00b-net-guest
```

実ゲームには所有する既知の専用コピーが必要。自己テストと CI ではゲームを起動しない。試験専用のローカル応答変更、無音・非表示の検証用 DLL は配布用の機能ではない。

## 使用バイナリ（SHA-256）

| ファイル | SHA-256 |
|---|---|
| 通常版 1.00b th12.exe（両コピー同一） | `D8D644D2E64957A3031B1A1399D0502E1DDAA5252D2C4E492770AD6717827628` |
| TH12Coop.dll（自己完結版） | `75F93F3F988094C5E4BA435DD40D7DE98DEADF0C3DDF878481FABEC911AA9C74` |
| TH12Coop.dll（結合・実ゲーム試験） | `62BA1BC43BD44B3ED562B9800BB321AFF2480FB0D86DBA69DEB96D131D5404CB` |
| TH12Coop.Phase1Tests.dll | `702DEE2A5494613E4585A5E305DB4EFEE85D52ED476A05EBF530E27042D5C26C` |
| TH12Coop.Rendezvous.dll（結合・実ゲーム試験） | `C1332D52CBF065F08F6058B1420EA610CD9AE236C4DC5D0BCC7FA31F4247227C` |
| TH12Coop.Rendezvous.dll（単体自己テスト） | `29C2A97621EFF5674EFA0B5454BB77EE2D9D4456D0F62AA82B23DB5684E47344` |
| Keryx.Ice.dll（自己完結版） | `D2D8EDFD0EB97FE540AFEC78DFE983E38C061281822D52AF3BEE7DFD438A57AD` |
| Keryx.Ice.dll（結合・実ゲーム試験） | `F3FD47F0ECBAA0F1066FFE7817048AD64C65ADFB35CB70FFCF61D02541EA8AC3` |
| th12coop-native.dll | `3397FBF9D8567D1570715EBFCA0ACA1C3758FBC3C515C0D87A403C05DCF9E793` |
| injector.exe | `DBEE4ACFAB56841832E35A9A7F1A541B0A2A4896FA16C5B067DD5D501E19D79C` |
| th12coop-quiet-test.dll（検証専用） | `1B20AEDD93BF33BFA2EAB150089AF0602BF759C65955A67D9C5551FA78CFBE10` |
