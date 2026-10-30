# 個別機体・入力・PC別設定の検証

2026-10-30に実施した開発版0.2.0の検証記録。通信Protocol 4、協力Rules 4。ベースは正式リポジトリ `moyu1254/th12co-op` の `c3c83c0`。公開済みタグ・リリースは変更していない。

実行結果の数値要約は [runtime-tests.json](runtime-tests.json)、最終ビルドとソースのSHA-256は [binaries.json](binaries.json)。原作データ、セーブ、セッション識別子、プロセスメモリーの調査結果は含めていない。詳細なローカル実行ログはGit対象外の `artifacts/feature-validation` に保持する。

## 実装と調査

- XInput／Windows joystickのデバイス選択、ボタン・軸・デッドゾーン・POV・キー設定を実装。各PCで操作ビットへ変換し、既存の確定フレーム通信へ送る。キーボード併用、非アクティブ時の中立化、切断時のキーボード継続を維持。
- 6装備の個別SHT、ANM slot 7/26、速度・判定・ショット・オプション・ボムのdispatchを実装。共有資源と単一のステージ進行を維持し、会話・ルートは1Pに確定。選択と個体の数値パラメーターを同期digestへ追加。
- 原作設定の60バイト構造と利用命令を照合し、画面・音量・入力・窓位置をローカル項目として保存。進行関連と未分類項目はオンラインで共通テンプレートへ正規化。保存時にオフラインの進行設定を保持。
- EXTRA／Practiceの既存メモリ内開放処理を維持。オンラインコピーに元スコア・リプレイを含めず、元のセーブを書き換えない。
- テスト専用DLLで副音声バッファを再生前に無音化し、不可視・非activationのゲームウィンドウを使用。通常DLL・配布マニフェストから診断DLLを分離。所有Jobとハンドルでテストプロセスの終了を確認。

設計は [個別機体](../../docs/independent-characters.md)、[入力](../../docs/local-input.md)、[PC別設定](../../docs/local-configuration.md)、[無音検証](../../docs/quiet-testing.md)を参照。

## ビルド・自己テスト

| 検証 | 結果 |
|---|---|
| x86製品DLL・injector、x64自己完結ランチャー | ビルド成功 |
| テスト専用smoke／quiet DLL | ビルド成功。既存smokeのinline assembler最適化警告C4740あり |
| ランチャー自己テスト | 18グループ成功。実TLS上の36通りの機体選択と改ざん拒否を含む |
| managed回帰 | 18グループ成功、失敗0 |
| Python所有プロセスの終了経路 | 10件成功。偽API、実ゲーム起動なし |
| Pythonテスト用コピーの制限 | 5件成功。合成設定と偽プロセス |
| smoke終了競合の回帰 | 3件成功。所有ハンドルの終了確認、未終了時のエラー保持、既終了時の不要な終了操作の防止 |
| quiet guard自己テスト | 成功。偽音声バッファと不可視所有窓。音声デバイスを開かない |
| 配布対象マニフェスト | 製品DLLを含み、smoke／quiet DLLを除外・拒否することを確認 |

[自己テスト出力](launcher-self-tests.txt)、[managed回帰](managed-audit.json)、[終了経路](native-cleanup.json)、[コピー制限](smoke-paths.json)、[smoke終了競合](smoke-cleanup.txt)。独立レビューは [managed設定](../managed-settings-review.md)と[非干渉ガード](../quiet-guard-review.md)。

## 実ゲーム

すべて同一の物理Windows PC上の隔離コピーで実行。入力のみの製品DLL試験と、状態を作るsmoke診断を区別する。ゲームは非表示で、音声バッファの無音化を確認した。Windows全体や他アプリの音量、マウス、キーボード入力、クリップボードを変更しない。

| 試験 | 結果・範囲 |
|---|---|
| 基本同期回帰 | 通常1.00a／通常1.00b／Steam1.00bそれぞれ1,100フレーム一致。射撃・移動・ポーズ復帰・P2ボム |
| 個別機体 Practice1 | 各版6組成功。0:5、1:4、2:3、3:2、4:1、5:0で両側の全6装備を含む。各1,108～1,248フレーム一致 |
| ローカル設定差 | 上記の個別機体試験で解像度・音量・入力割当・窓位置を変えた2プロセスが一致 |
| 個別オプション Practice2 | 通常1.00aの6組成功。各1,132～1,272フレーム一致。装備固有のオプションscript・個数・座標を両actorで照合 |
| EXTRA・通常版／Steam版の相互接続 | 通常1.00bホスト／Steam1.00bゲストの6組成功。各1,060～1,200フレーム一致。各自4個のオプション、双方の射撃・ボム完了を確認。両側スコアなしのコピーから開始 |
| 実TLSのゲーム同期 | 通常1.00b／Steam1.00b、霊夢B／早苗A、60秒。3,307フレーム確認済み、両者の射撃・P2移動、無音・不可視・終了、設定とスコアの保持を確認 |
| 通信断の後片付け | 同じ版・機体、60秒／3,319フレーム確認済み。ゲストだけを切断し、51msで両所有ゲームの終了を確認 |
| ランチャーの所有管理 | 通常1.00bのオフライン起動、既存ゲームがある場合の二重起動拒否、ランチャー自然終了後のゲーム消滅を外部runnerで確認。設定・スコアのハッシュ保持 |
| 共有ルールのsmoke回帰 | 3版×6件＝18件成功。共有残機、ダメージ合算・軽減、二重回収防止、衝突、弾digest、合法入力mask。全件exitCode 0・所有ハンドル終了、ダメージ診断後のネイティブ更新継続を確認 |
| 同プロセスの機体再生成 | 通常1.00a、Practice2、霊夢A／早苗B。1,881フレーム一致。リトライ、P2ボム発動中のタイトル移行、両actorとANM26の解放、再入場後の装備・射撃・オプション |
| 起動前の到達同期 | 最終injectorで3版各30回、計90回成功。ゲーム本体未実行、音声／窓／D3D作成0、所有プロセス終了確認 |

各試験で個別SHTの速度・判定半径、ANM slot、移動量、双方のショットと発動者別ボムを検査。オプション観測追加後の試験は、装備固有scriptと個体ごとのANM所有者・個数・座標を照合する。古い試験に後から追加したassertionを適用済みとは扱わない。各記録の `independentNativeOptions` と `lifecycle` が実施範囲を示す。

## 失敗からの修正

1. 非入力の専用デスクトップではD3D初期化が停止した。所有プロセスを終了し、この試行を成功に含めない。現在は使用中デスクトップに不可視・非activation窓を作り、D3Dのウィンドウ変更を禁止する。
2. 初期quiet guardがDirectSoundの通知インターフェースを返さず、BGMのポーズ復帰が失敗した。通知専用ラッパーで元の再生時計と通知を維持し、無音バッファ外へ音声インターフェースを漏らさない形へ修正。修正後の3版基本回帰に成功した。
3. 連続起動中、entry到達前の最初の `GetThreadContext` がエラー31を返す失敗を2件記録。WOW64初期化中の取得不能は推定であり、エラー番号だけで内部状態を断定しない。初期化途中のcontext取得を前提にせず、既知entryから専用のRX駐車コード／RW到達フラグへ移り、到達を確認後に停止位置を取得する方式へ変更した。元entry復旧は停止完了と正確な位置を確認した成功経路だけで実施。失敗経路はfenceを保ち、所有プロセスを終了する。最終方式で90回の起動前試験に成功。旧失敗記録を削除・成功へ置換していない。
4. 既存cfgで音声OFF・保存音量非ゼロの場合、画面設定だけの保存で音声を再有効化する問題を修正。実効音量を表示し、音量を変更した項目だけ更新する。自己テストで既存設定の保持を確認。
5. 最初の実TLSゲーム試験はランチャー本体が成功したが、外部runnerの文字エンコーディング名の誤記で結果の読み込みに失敗した。元の成功レポートとrunnerの失敗記録を保持し、reader修正後に新しい出力先で再実行した。
6. 既存のダメージ診断fixture（family 1 / case 3）がボス不在でスペル有効フラグを残し、後続の原作スペル更新がNULLのボスを参照した。診断のダメージ照合中だけスペル番号・フラグを変更し、直後に復元する修正を行った。この変更は `COOP_SMOKE_TEST` 内に限定する。また、smoke側の終了競合が元の例外を隠す問題を修正し、所有ハンドルの終了を確認した場合だけ競合を受け入れ、非ゼロ終了コードと元の試験結果を記録するようにした。

診断修正前は計算結果のmaskが0でも後続更新で `0xc0000005` になり失敗した。修正後の新しいsmoke DLLでは3版すべてで計算と後続更新が成功し、代表18件も全件成功した。製品DLLには診断fixtureを含めていない。最終確認時点で `th12`・`injector`・テスト用 `TH12Coop` プロセスの残存は0件。

## 未検証・制約

- 別の物理PC、WAN、異なるGPU／ドライバー間の同期。ループバックTLSの成功をWAN確認とはしない。
- 物理パッドの押下、抜き差し、Bluetooth接続、複数同型機の識別。開発PCではXInput 1枠とWinMM 1枠の列挙・中立状態読取のみ実施。同一デバイスが両APIに出る場合がある。
- 入力設定ダイアログの実操作、通常表示／全画面での画像・ANMの目視確認、可聴音質。検証中のPC操作を妨げないため実施していない。
- 実ゲームの全36組の直積、混在機体での全6面・EXTRA全スペル・エンディングまでの通しクリア、長時間のパワー増減・死亡復帰・残機枯渇と全装備の組み合わせ。
- 原作デモ・リプレイの個別選択除外は静的レビュー。ゲーム状態digestは既存の部分的な正規化を拡張したもので、全メモリー状態の一致証明ではない。
- 非表示・windowed・音声時計を維持する無音バッファという検証条件がある。通常表示と通常音声協調レベルの確認には追加の実操作が必要。

この検証を実施した時点では公開リリースを作成していない。配布ZIPの新規作成・GitHub公開・CIの実行は、このローカル検証の結果に含めない。

## 再実行

所有する既知の原作を用途別の `game-test-*` コピーへ用意し、既存の記録を上書きしない新しい `--out` を指定する。以下はこの検証で使用した実行方法の例。最初に製品と診断用の両方をビルドする。

```powershell
.\build.ps1 -SelfContained
.\build.ps1 -NativeOnly -SmokeTest
.\scripts\Test-Repository.ps1 -RunSelfTest
python scripts/test_smoke_cleanup.py
python scripts/test_loader_fence.py --game game-test-retail-1.00a-net-host --count 30 --out artifacts/new-run/loader.json
python scripts/test_native_characters.py --host-game game-test-retail-1.00a-net-host --guest-game game-test-retail-1.00a-net-guest --mode practice --stage 2 --pairs 0:5 --different-local-settings --lifecycle --out artifacts/new-run/lifecycle
python scripts/test_native_characters.py --host-game game-test-retail-1.00b-net-host --guest-game game-test-steam-1.00b-net-guest --mode extra --different-local-settings --out artifacts/new-run/extra
python scripts/test_managed_gameplay.py --game game-test-retail-1.00b-net-host --guest-game game-test-steam-1.00b-net-guest --out artifacts/new-run/managed
python scripts/test_cooperative_regressions.py --versions retail-1.00a,retail-1.00b,steam-1.00b --case 1:0:0 --case 1:3:0 --case 3:6:0 --case 2:103:0 --case 5:5:0 --case 5:7:0 --out artifacts/new-run/regressions
```
