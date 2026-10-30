# オンライン当たり判定マーカーの描画修正

2026-10-30、v0.3.1取込前の試作ソースで実施した履歴。下記は通信4・協力ルール5・IPC 1での結果であり、配布用v0.3.2の結果は [v0.3.2の検証記録](../releases/v0.3.2.md) を参照する。

## 変更と確認方法

ランチャーが既存IPCヘッダーの予約領域+120へローカル役割（ホスト1、ゲスト2）を注入前に渡す。原作のマーカー生成後 `0x43660B`（1.00bでは+0x20）で相手側のANMファミリーに描画コールバックを設定し、描画直前に可視ビットを落とす。`bullet.anm` のスクリプト77と子76の両方が対象。更新・低速解除時の割込み・消滅は原作が処理し続ける。

`scripts/test_native_focus.py` は製品DLLと既存のquiet-test DLLを使い、用途別の隔離コピーを不可視・無音で起動する。入力IPCで進め、ANMリスト・スクリプト番号・所有元の座標・描画コールバック・可視ビットを読み取る。スクリーンショットによる目視判定ではない。

## 実ゲームの結果

| EXE | 機体 P1 / P2 | 結果 | 一致した同期フレーム |
| --- | --- | --- | --- |
| 通常1.00a | 霊夢A / 早苗A | PASS | 787 |
| 通常1.00b | 霊夢B / 魔理沙B | PASS | 787 |
| Steam 1.00b | 魔理沙A / 早苗B | PASS | 787 |

各試験はP1側とP2側の2プロセスで、双方低速、低速解除直後の4フレーム×3回、再低速×3回、マーカー消滅、片方ずつの低速を確認した。双方低速時には両者の76/77が存在し、自分側の2つは可視、相手側の2つは非可視だった。解除中に残る相手のANMも非可視を維持した。同期ハッシュは一致し、quiet制御の失敗0、可視ウィンドウ0、フォアグラウンド所有なし、所有するゲームハンドルの終了を確認した。

配布済みv0.3.0の製品DLLでも同じ通常1.00a試験を実行した。607フレーム目の双方低速で、相手側の76/77も可視となって期待どおりFAIL。修正前の症状を検出できた。指紋は [結果一覧](results.json) を参照。

成功結果はツール出力で確認した要約を本記録に残した。後続試行で同じ作業用JSONを上書きしたため、成功時の全サンプルJSONは保持していない。ローカル調査ダンプやセッション情報は収録しない。

## ビルドと制約

`build.ps1 -NativeOnly`、`build.ps1 -NativeOnly -SmokeTest`、`build.ps1 -LauncherOnly -SelfContained` は成功。damage-budget、fiber-FPU、quiet-guardの自己テストも成功。smokeビルドには既存の `coop_audit_targeting.h` のC4740警告がある。

最終の `scripts/Test-Repository.ps1 -RunSelfTest` は成功（追跡済み452ファイルの検査、19 test groups、managed regressions 19 groups）。新規Pythonの構文検査と `git diff --check` も成功。管理側回帰には、IPC+120へ0/1/2を正しく公開し3を拒否する検査を含む。最初のフレームワーク依存ランチャーでの自己テストは150秒でタイムアウトしたため、ランタイム同梱で再ビルドした最終結果と区別する。

3版の成功試験の後、Windows セキュリティが `dist/injector.exe` を検出・隔離し、後続試行は注入用プロセスの起動時にWin32エラー225で停止した。セキュリティ設定の変更、除外、復元は行っていない。役割0の後方互換試験、実際のオフライン描画、TLS接続を通したランチャーの役割伝達の追加実ゲーム試験は未完了。最後の項目の観測チェックは `LockstepTest.cs` に追加したが実行できていない。

通常表示での目視品質、別PC/WAN、被弾・復帰やステージ移行を含む長時間プレイは今回の対象外。

## 再実行例

```powershell
py scripts/test_native_focus.py --host-game game-test-retail-1.00a-net-host --guest-game game-test-retail-1.00a-net-guest --out artifacts/focus/new-retail-1.00a.json
py scripts/test_native_focus.py --host-game game-test-retail-1.00b-net-host --guest-game game-test-retail-1.00b-net-guest --p1 1 --p2 3 --out artifacts/focus/new-retail-1.00b.json
py scripts/test_native_focus.py --host-game game-test-steam-1.00b-net-host --guest-game game-test-steam-1.00b-net-guest --p1 2 --p2 5 --out artifacts/focus/new-steam-1.00b.json
```
