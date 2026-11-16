# 部屋番号・Phase 1 の検証

2026-11-16、Windows 11 Home (build 26200)、.NET SDK 10.0.403 / runtime 10.0.14。Protocol 5、Rules 6、IPC 1。ローカル試験用のHTTPS証明書は各実行のクライアントだけで照合し、Windowsの信頼ストア、実ルーター、ファイアウォールは変更していない。

| 試験 | 実行内容 |
|---|---|
| ビルド・配布境界 | `build.ps1 -SelfContained`、`build.ps1 -NativeOnly -SmokeTest`、リポジトリ検査。配布処理の合成試験6件（現行形式の招待を含むドキュメントを拒否）。リリースZIPは作成しない。 |
| ランチャー | `scripts/Test-Repository.ps1 -RunSelfTest`。自己テスト23グループ（内部のmanaged検証19グループ）。旧招待・通信4・接続方式違い、不正な照合情報、片側だけの承認・拒否、誤ったpin・参加鍵、候補・フレーム検証を含む。 |
| 合流サーバー | `dotnet run --project src/rendezvous -c Release -- --self-test`。実際のHTTPSで単回取得・同時取得競合、所有者による取消、偽の取消鍵、偽の転送元ヘッダー、旧招待、過大本文を検証。模擬時計で10分失効、件数とIPv4・IPv6 /64・全体の頻度制限を検証。 |
| 結合 | `dotnet run --project src/phase1-tests -c Release`。実際のKestrelとランチャーの番号クライアントで、発行→取得→peer間TLS→両者の照合→データ送信。使用済み・待機中に取り消した番号を再取得できず、ホストの転送を照合前／取消時に解除することを確認。 |
| UPnP | 模擬IGDと実際のSOAP要求処理で、有限リース、既存・置換済み転送の保護、取消時の解除、永続リース・ゲートウェイ外のURLの拒否を検証。SSDPによる実機検出やISP越しの到達性は試していない。 |

## 実ゲームと再現手順

通常版1.00bのホスト・ゲスト用の隔離コピーを使用。診断入力は固定機体1・4、ゲームは非表示・無音で起動し、実際のゲームプロセスのロール、入力・digest、ステージ到達、射撃と移動、終了を検証する。照合はテストの両コールバックで実際のコードの一致を調べる。人間による通話照合やウィンドウ操作はこの結果に含まない。

```powershell
.\build.ps1 -SelfContained
.\build.ps1 -NativeOnly -SmokeTest
dotnet publish src/phase1-tests -c Release -o build
Copy-Item dist/th12coop-native.dll,dist/th12coop-quiet-test.dll,dist/injector.exe,dist/th12-online.cfg build/
dotnet build/TH12Coop.Phase1Tests.dll --games game-test-retail-1.00b-net-host game-test-retail-1.00b-net-guest
```

| 接続環境 | 対象 | 結果 |
|---|---|---|
| 同一PC・IPv4ループバック | 番号方式、模擬転送、通常版1.00bの実ゲーム2プロセス | 60秒で両者3,302フレーム確定、ステージ到達、両者の自機弾を最大各24発観測。照合後に起動し、終了時に両ゲームが停止。診断入力・無音ガードの失敗なし |
| 同一PC・IPv4ループバック | 長い招待で直接接続。自己完結版を `scripts/test_managed_gameplay.py --cases disconnect` で検証 | 60秒で両者3,432フレーム確定。ゲスト側を切断して135 msで両側が終了。隔離コピーの設定・スコアを保持 |
| 別PCのLAN・IPv6直通 | IPv4/IPv6、同一ルーターでのNATループバックを含む | 未検証 |
| 別回線（WAN） | フルコーン、制限／ポート制限、シンメトリックNATの各組合せ × IPv6有無 × 実UPnP可否 | 全組合せ未検証。接続可能と判定した組合せはない |

この変更での実ゲーム検証は通常版1.00bだけ。通常版1.00a・Steam版1.00b、別PC間のゲーム起動、物理入力、全ステージ、ルーターによる転送解除後の既存TCPの維持は未検証。

## 使用バイナリ（SHA-256）

| ファイル | SHA-256 |
|---|---|
| th12.exe（通常版1.00b、両コピー同一） | `D8D644D2E64957A3031B1A1399D0502E1DDAA5252D2C4E492770AD6717827628` |
| dist/TH12Coop.exe | `7941EBB031DEFE2FFEDB1D14B088E903D275D46EC6A98A026A30ACB7EA214417` |
| dist/TH12Coop.dll（自己完結版） | `D2F937BD7D0AA619C0DE7C75A5E66C54691D9CFA3EF4A8ADC5C4C4F5EDDA3CFA` |
| build/TH12Coop.dll（番号結合試験、同じソースのフレームワーク依存版） | `41AAAB933844B3D1E52DB1204B53FBF318D9F6F390975E59DFEBE5B8811A5A0C` |
| TH12Coop.Phase1Tests.dll | `A134319D473BD8CBD4008CD252FDD51CE584CD8D195BABBDA89E09200E075485` |
| TH12Coop.Rendezvous.dll | `3423A2EBF1D1A60C94F44F2632922B7FD3BAC3D4DE6191C6E18341F1DE07F65C` |
| th12coop-native.dll（製品DLL） | `9089DEE918CB16B08A7E6F9FC2D0010134CEE255B7B948B41DD418002BE5AB2C` |
| injector.exe | `EB0BF0476262A3FB628B011E634A03B6A78C0C9DC4F0B69C0E3B022D709C501C` |
| th12coop-quiet-test.dll（検証専用） | `567B0E506CA97E6E82FFEE4F1F050C201E8D7C30B6295D1A32B8B9B5965A900B` |

インジェクターの指紋は実ゲーム試験前に取得した。番号方式の検証後、Windows Defender がローカルでビルドした `build/injector.exe` と `dist/injector.exe` を `Behavior:Win32/DefenseEvasion.A!ml` として検出し、再読取りが拒否された。このPRにインジェクター・native DLLのソース変更やバイナリ配布はなく、検出原因は未判定。除外設定や復元は行っていない。
