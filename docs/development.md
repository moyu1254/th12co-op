# 開発・リリース

開発先は [`moyu1254/th12co-op`](https://github.com/moyu1254/th12co-op) です。ソースは [MIT License](../LICENSE) で提供し、原作とランタイムの権利は [NOTICE](../NOTICE.md) に記載します。原作の実行ファイル・データ・スコア、作業用のゲームコピー、ビルド出力、秘密情報はコミットしません。配布ZIPはGitHub Releasesへ添付します。

## ビルドと検証

ビルドにはWindows、Git、Visual Studio C++ Build Toolsのx86コンパイラー、Windows SDK、.NET 10 SDKを用意します。PythonテストにはPython 3.10以降も必要です。オンライン自己テストには、TLS 1.3に対応したWindows 11以降、またはWindows Server 2022以降が必要です。

```powershell
git clone https://github.com/moyu1254/th12co-op.git
cd th12co-op
.\scripts\Test-Repository.ps1
.\build.ps1 -SelfContained
.\scripts\Test-Repository.ps1 -RunSelfTest
```

この手順に原作データは不要です。製品DLL、インジェクター、x64ランチャーをビルドし、ランチャーの通信・入力検証・IPC自己テストを実行します。自己テストはゲームを起動しません。検証スクリプトがランチャーの終了を待ち、終了コードとその実行で生成された結果ファイルを確認します。

上の例は既定ブランチのソースを取得します。配布済みv0.2.0を再現する場合は、ビルド前に `git switch --detach v0.2.0` を実行してください。開発に参加する場合は変更対象のブランチから作業ブランチを作成してください。リリースタグは配布済みのソースと検証記録を示すため、書き換えません。

`Test-Repository.ps1` はGitで追跡するファイルの配置・拡張子、バイナリ混入、固定設定の指紋、PowerShell構文、代表的な秘密鍵・GitHubトークン・ルームコードの形式を検査します。任意の秘密情報や過去のGit履歴を網羅する検査ではありません。コミット前には `git diff --cached` も確認してください。新しいソース形式を追加するときは、検査の許可リストも明示的に更新します。

Windows CIは同じ検査と `build.ps1 -SelfContained`、`--self-test` を実行します。原作の起動、注入処理、描画、全ステージ、別PC間のオンライン動作はCIの対象外です。実ゲーム検証は所有する対応版を専用コピーへ準備して行い、使用したバイナリ指紋と実測結果を [検証記録](../verification/RESULTS.md) に残します。

CIは `contents: read` のみを使用し、認証情報をcheckout後に保持せず、リリースを自動公開しません。Actionsは確認した公式リリースの完全なコミットSHAで固定しています。更新時は [checkout](https://github.com/actions/checkout/releases)、[setup-dotnet](https://github.com/actions/setup-dotnet/releases)、[GitHubのセキュリティ指針](https://docs.github.com/en/actions/reference/security/secure-use) を確認してください。Windowsランナーのコンパイラー構成は [公式イメージ一覧](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md) で確認できます。

配布処理の回帰は `python scripts/test_package.py` で実行します。使い捨てのGitリポジトリと合成ファイルを使用し、未追跡ファイルと診断DLLの除外、ライセンスの同梱、検証マニフェストとの一致を確認します。原作は起動しません。

## リリース

v0.3.1は協力ルール6。死亡復帰時の共有ボム補充を原作どおりに戻すため、ルール5以前との接続を拒否する。オンラインの表示はローカル自機を基準にする。通信プロトコル4とローカルIPC 1は維持する。公開済みv0.3.0のタグ・配布物・検証記録は変更しない。

v0.3.0は協力ルール5です。通信プロトコル4とローカルIPC 1は維持し、火力補正とゲーム内選択の互換性はルール番号で分離します。v0.2.0との接続は拒否します。公開済みタグ・リリース・ZIPを置き換えません。[設計](cooperative-damage.md)と[Rules 5の検証](../verification/rules5/RESULTS.md)を参照してください。

現行リリースは `0.3.1`（Pre-release）、通信番号4、協力ルール番号6です。被弾後のボム補充修正とオンラインの視認性改善を含みます。[リリースノート](releases/v0.3.1.md)を参照してください。旧版の配布物・タグと過去の検証記録は保持します。

実ゲームの自動検証前には `build.ps1 -NativeOnly -SmokeTest` も実行します。音声バッファを無音化し、ウィンドウを非表示にするテスト専用DLLをビルドします。同時に、音声デバイスを開かずにガード機能を確認する自己テストを実行します。

実ゲームテストは用途別の `game-test-*` コピーまたは隔離セッションだけで実行します。フォーカス・入力・クリップボード・Windows音量は変更しません。描画検証への影響を含む詳しい制約は [無音・非干渉テスト](quiet-testing.md) を参照してください。

初回の `v0.1.0` は Rules 2、修正版 `v0.1.1` は Rules 3 の **Pre-release** です。製品のバージョン番号と協力プレイのルール番号は別の番号です。通信・ルール・設定の互換性が変わる修正では、対応するバージョン判定と検証も更新します。

リリース対象のコミットでCIと変更に応じた実ゲーム検証を完了し、対応版・実施済み検証・既知の制約をリリースノートへ記録します。

`scripts/Package.ps1` でZIPを作成し、`python scripts/verify_package.py --archive <ZIP> --out build/package-check.json` でソースとの内容一致・構造・ランタイムを検査します。原作データやsmoke DLLの混入がないこと、展開後の起動、SHA-256も検証します。検証したZIPをそのまま添付し、CIの再ビルド出力へ差し替えません。

パッケージ作成と検証にはGit checkoutが必要です。ソース・仕様・検証記録はGitの追跡対象だけを収録し、フォルダー内に置いた未追跡ファイルは含めません。追加ファイルは `git add` してから検査し、`git diff --cached` で内容を確認してください。MITライセンスとNOTICE、実際に同梱するランタイムのライセンスもZIPに含めます。パッケージ作成はGitHubへのアップロードを行いません。

以下はリリース操作の雛形です。`<...>` を未使用のタグ、検証済みZIP、SHA-256一覧、そのZIPに対応するコミットとルール番号へ置き換えてください。リリースノートは `docs/releases/<タグ>.md` に用意します。タグは必ず検証対象のコミットへ付け、修正は新しいバージョンとして配布します。公開済みのタグや添付ZIPは上書きしません。

```powershell
$tag = '<未使用のリリースタグ>'
$validatedCommit = '<検証対象の完全なコミットSHA>'
$zip = '<検証済みZIPのパス>'
$checksums = '<SHA-256一覧のパス>'
$rules = '<検証した協力ルール番号>'
$notes = "docs/releases/$tag.md"
Get-FileHash -LiteralPath $zip -Algorithm SHA256
Get-Content -LiteralPath $checksums
# 指紋と検証記録、ZIPとリリースノートを確認してから実行。
git tag -a $tag $validatedCommit -m "TH12 CO-OP $tag (Rules $rules)"
git push origin $tag
gh release create $tag $zip $checksums --repo moyu1254/th12co-op --verify-tag --prerelease --latest=false --title "TH12 CO-OP $tag" --notes-file $notes
gh release view $tag --repo moyu1254/th12co-op
```

`--verify-tag` はリモートにタグがなければリリースの作成を中止します。Pre-releaseの扱いは実際の安定性と検証範囲に合わせて決めます。操作の詳細は [GitHub CLIのrelease create](https://cli.github.com/manual/gh_release_create) を参照してください。
