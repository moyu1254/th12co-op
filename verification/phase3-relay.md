# Phase 3：部屋番号の中継フォールバック

2026-11-16。通信 Protocol 7、協力 Rules 6、ローカル IPC 1。Windows 上の同一 PC にテスト用の HTTPS/WSS サーバーを起動し、二つのランチャーの通信処理を実行した。外部へのデプロイは行っていない。

| 条件 | 確認内容 |
|---|---|
| 直接 UDP が使える | ICE・QUIC で接続して照合後にレコードを往復。両側とも WSS 要求は0件。 |
| 片側の候補がない | ホストの IP 証明を取得できない条件で、同じ番号から双方が WSS に1回ずつ接続。ゲスト側だけの IP 証明が失敗する場合は下記の実ゲームで検査。 |
| 候補はあるが到達不能 | テスト用ディレクトリの応答で両側のポートを応答しないループバックソケットに置き換えた。実際の ICE チェックの後に WSS で照合し、4096 bytes のレコードと応答を交換。 |
| ICE は成功するが QUIC が通らない | 模擬 NAT の選定済み経路で QUIC Initial を破棄。ホスト・ゲストの両方が中継の対象となる到達性エラーを返すことを確認。 |
| 片側だけ直接経路が使える | クライアントの QUIC Finished が届かない場合も両者が到達性エラーを返す。別の結合試験では、直接 QUIC で認証を終えたゲストが経路の確定を待ち、ホストが到達失敗を報告すると、同じ部屋の中継で両者が再認証してレコードを交換した。 |
| ICE が登録外の IP を選定 | 選定した相手の IP が通知済みの候補に含まれない場合、その経路を拒否し、中継を選べることを確認。 |
| 参加認証・終了 | 直接 QUIC と中継内 TLS で、証明書や参加鍵が違えば検証する側で認証失敗。WSS で両者の照合コードが一致し、片側だけの承認では開始しない。送信直後に閉じても最後のレコードが届き、その後にストリームの終了を検出。 |
| 接続制限と解放 | 応答登録前・役割違い・二重接続・使い直しを拒否。実際の WSS ソケットで、断片を合算して16 KiBちょうどは通り、超過時はその一部も相手に渡らないことを確認。小さいペア数・転送量・期限を設定し、枠不足、双方向合算の上限、片側待ちと稼働中の失効、切断後の枠の再利用を検査。 |
| 部屋の期限・送信元の枠 | 公開期限をまたぐ参加・応答・中継予約が成功し、再送でも各期限が延びないことをテスト用時計で確認。送信元の同時上限2・4・6組、同じ NAT の両者を1組と数えること、待機取消後の枠の再利用を検査。 |
| 終了時の逆方向の配送 | 一方が Close を送った3秒後に、もう一方が送信中メッセージの最後の断片を送信。逆方向のメッセージ全体と両方向の Close が届いた。別試験では TLS 終了処理と WebSocket の Close 応答をそれぞれ650 ms遅らせ、各1秒の期限が独立して働くことを確認。 |

## 実ゲームと未検証範囲

初回の Phase 3 実装では、通常版 1.00b の専用コピー2つを、片側の IP 証明だけが取得できない条件で接続した。ホストとゲストの中継要求は各1回。60秒で両者ともステージに到達し、3,362フレームまで一致した。1P・2P の自機弾を観測し、通信エラーは0件。両プロセスは非表示・無音の検査を通り、終了を確認した。

レビュー修正後、同じ2つの専用コピーで各経路を60秒ずつ動かした。両試験ともホスト・ゲストがステージに到達し、1P・2P の自機弾と 2P の横移動を観測した。両プロセスの非表示・無音検査と、試験終了後の終了確認に合格した。

| 経路 | ホストの確認フレーム | ゲストの確認フレーム | 中継要求（ホスト／ゲスト） | 通信エラー |
|---|---:|---:|---:|---:|
| 直接 QUIC（双方が認証完了を申告） | 3,248 | 3,248 | 0／0 | 0 |
| WSS 中継（ゲストの IP 証明を取得できない条件） | 3,384 | 3,384 | 1／1 | 0 |

| 接続環境 | 判定 |
|---|---|
| 同一 PC、IPv4 ループバック、通常版 1.00b 同士 | 直接 QUIC と WSS 中継で接続可。 |
| 別 PC・同一 LAN、実機の UPnP | 未検証。 |
| 別 PC・別回線：フルコーン／制限／ポート制限／シンメトリック NAT の各組み合わせ × IPv6 有無 × UPnP 可否 | すべて未検証。模擬環境での直接接続失敗や中継成功から、個別の WAN 環境の成否は判定していない。 |

通常版 1.00a・Steam 版での中継、全ステージ、通話での人手照合、公開サービスの運用・負荷・費用は今回の検証外。

Windows 11 Home（build 26200）、.NET SDK 10.0.403。自己完結版のビルド、ランチャー自己テスト23グループ（内部の managed 回帰19グループを含む）、ネイティブ診断ビルド、配布処理の合成テスト6件も合格した。

## 再現手順

```powershell
.\build.ps1 -SelfContained
.\build.ps1 -NativeOnly -SmokeTest
.\scripts\Test-Repository.ps1 -RunSelfTest
py scripts/test_package.py

dotnet publish src/phase1-tests -c Release --artifacts-path build/review-final-obj -o build
Copy-Item -LiteralPath dist/th12coop-native.dll,dist/th12coop-quiet-test.dll,dist/injector.exe,dist/th12-online.cfg -Destination build
dotnet build/TH12Coop.Rendezvous.dll --self-test
dotnet build/TH12Coop.Phase1Tests.dll
dotnet build/TH12Coop.Phase1Tests.dll --games game-test-retail-1.00b-net-host game-test-retail-1.00b-net-guest
dotnet build/TH12Coop.Phase1Tests.dll --relay-games game-test-retail-1.00b-net-host game-test-retail-1.00b-net-guest
```

実ゲームには所有する既知の専用コピーが必要。自己テストと CI ではゲームを起動しない。試験専用のローカル応答変更、無音・非表示の検証用 DLL は配布用の機能ではない。

## レビュー修正後の使用バイナリ（SHA-256）

サーバー自己テスト、結合試験、実ゲーム試験には同じ publish 出力を使った。初回実装のハッシュは[初回の検証記録](https://github.com/moyu1254/th12co-op/blob/960b6806f0cfa213fb3c5fc13153e13643fb4dfe/verification/phase3-relay.md)に記載している。

| ファイル | SHA-256 |
|---|---|
| 通常版 1.00b th12.exe（両コピー同一） | `D8D644D2E64957A3031B1A1399D0502E1DDAA5252D2C4E492770AD6717827628` |
| TH12Coop.dll（自己完結版） | `F355FE925D65ADE1C0C11F7EA96C1E952C12A1FBF93F426E3BF6522E653B9D56` |
| TH12Coop.dll（結合・実ゲーム試験） | `3AAF56A0EEA2ABE1CF8FFCF518408B0031BB4A9F890BFB4220392B7B5355D944` |
| TH12Coop.Phase1Tests.dll | `3D4CBE796414D4579A779E3DF64BC4464986195B036FE81B375B0F56309F5E9F` |
| TH12Coop.Rendezvous.dll（単体・結合・実ゲーム試験） | `366C8E5060DBEBC941AE14A3899E69D95B61FEF8E56ADC8B84ED538EF1E2477E` |
| Keryx.Ice.dll（自己完結版） | `69C1AA15BEB9546DEBC2A5F47FCB439C0AAF30D87B678C4998FB12294A3C8B97` |
| Keryx.Ice.dll（結合・実ゲーム試験） | `2431554F0DF7774680FEA43982CB7CA1B1B0F39E6FA7EF8FF5FFF4F345844A2A` |
| th12coop-native.dll | `8D267696CED7A01A542C2085FED33E3EA96B840D68E4806A90A59008FB5C4747` |
| injector.exe | `2BAA6C4C01DB4ED18277336FA823FE345967FDDE8DEFFD985602675A4FB81612` |
| th12coop-quiet-test.dll（検証専用） | `9840077669942D3E2F451A5A518601045761B5730B5816A07FA145D8282F1232` |
