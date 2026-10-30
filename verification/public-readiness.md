# ソース公開に向けた整理の検証

2026-10-30。配布済みv0.2.0のソース（`1e956f2`、通信4／協力ルール4）を基準に、公開用の資料・ライセンス・配布処理を整理した。`src/` のゲーム実装は変更していない。既存タグ・リリース・配布ZIPは変更していない。

## 変更

- MITライセンス、原作と同梱ランタイムを区別するNOTICE、参加ガイド、資料の索引を追加。
- READMEから導入・検証範囲・開発資料への導線を整理。ビルド時に原作を準備する不要な手順と開発PC固定パスを除去。
- 配布スクリプトと検証マニフェストをGit追跡対象へ限定。ソースフォルダーの未追跡ファイルを再帰コピーする問題を修正。LICENSEとNOTICEを同梱。
- `game*`、セーブ、リプレイ等の無視規則を補強。暗号化秘密鍵の代表的なヘッダーを既存の検査へ追加。
- 配布処理の回帰5件と既存のsmoke終了競合3件をWindows CIへ追加。

## 実行結果

| 検証 | 結果 |
|---|---|
| `scripts/Test-Repository.ps1` | 追跡ファイルの許可規則、UTF-8、固定cfg指紋、代表的な秘密情報形式、PowerShell構文に合格 |
| `build.ps1 -SelfContained` | x86製品DLL・injector、x64ランチャーのビルド成功。MSVC、SDK 10.0.403、ランタイム10.0.14 |
| `scripts/Test-Repository.ps1 -RunSelfTest` | ランチャー18組、managed回帰18組成功。実TLS・機体36組・IPCを含む。原作は起動しない |
| `scripts/test_native_cleanup.py` | 偽APIによる所有プロセス終了10件成功 |
| `scripts/test_smoke_paths.py` | 合成設定と偽プロセスによるコピー境界5件成功 |
| `scripts/test_smoke_cleanup.py` | 偽APIによる終了競合3件成功 |
| `scripts/test_package.py` | 使い捨てGit checkoutで5件成功。未追跡ソースと診断DLLの除外、原作形式の追跡拒否、ライセンスの追跡必須、欠損入力の拒否、既存ZIPの保持 |
| 修正前との比較 | 同じ未追跡ファイル混入テストが修正前のPackage.ps1では失敗し、修正後は成功 |
| 確認用ZIPの検証 | `Package.ps1` と `verify_package.py` で内容一致、構造、PEアーキテクチャ、ランタイム、ライセンス同梱、診断DLL・原作データの除外に合格。新規リリースとしては配布していない |
| Markdownリンク | 追跡資料内の相対ファイルリンクに欠損なし |

履歴検査は作業開始時のローカル全refsから到達可能な4コミット・494種類のblobを対象に、ファイル許可規則、原作cfgの指紋、テキスト形式と代表的な秘密情報パターンを確認した。検証JSONのセッション識別子・招待・認証情報を示す代表的なキーも検査し、該当はなかった。任意の秘密情報、GitHub上だけの未取得refs、過去に削除した到達不能オブジェクトまで網羅する検査ではない。

詳細なローカルレポートはGit対象外の `build/public-review.json`、`build/public-package-check.json`、`build/public-native-cleanup.json`、`build/public-smoke-paths.json`。確認用ZIPもGit対象外の `release/` に作成した。

使用したビルドのSHA-256：

| バイナリ | SHA-256 |
|---|---|
| `TH12Coop.exe` | `f0dae2d99eb0e4c3ea4f768a060c896856b1cce3b112d66de4124cf52b6cb4a7` |
| `TH12Coop.dll` | `974d202c8332e6807039625cb74bcc21d949b7c3d66dddc6ea1f03331d215576` |
| `th12coop-native.dll` | `76a48c19d2c0d6e64ad1bcfbe626ea757b5b4a7efa35a906901ce79e082eb29d` |
| `injector.exe` | `5ad963fe398e7e2fa73a13c47dc5dfedd3f4e792b745caf2e17683c06b7d29e7` |

## 実ゲームの扱い

ゲーム実装・通信形式・協力ルールを変更していないため、この整理作業では実ゲームを再実行していない。実ゲームの使用バイナリと確認済み範囲は [v0.2.0の記録](features-v0.2.0/RESULTS.md) を参照。別PC・WAN・物理パッド・通常表示と可聴音声・全組み合わせでの全編クリアは、この作業でも未検証のまま。
