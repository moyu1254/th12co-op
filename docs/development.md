# 開発・リリース

開発先は非公開の [`moyu1254/th12co-op`](https://github.com/moyu1254/th12co-op) です。原作の実行ファイル・データ・スコア、作業用のゲームコピー、ビルド出力、秘密情報はコミットしません。配布ZIPはGitHub Releasesへ添付します。

## ビルドと検証

Windows、Git、Visual Studio C++ Build Toolsのx86コンパイラーとWindows SDK、.NET 10 SDKを用意します。オンライン自己テストにはTLS 1.3対応のWindows 11以降、またはWindows Server 2022以降が必要です。GitHub CLIでアクセス権のあるアカウントへログインしてから取得します。

```powershell
gh repo clone moyu1254/th12co-op
cd th12co-op
.\scripts\Test-Repository.ps1
.\build.ps1 -SelfContained
.\scripts\Test-Repository.ps1 -RunSelfTest
```

この手順に原作データは不要です。通常DLL、インジェクター、x64ランチャーをビルドし、ランチャーの通信・入力検証・IPC自己テストを実行します。自己テストはゲームを起動しません。GUI実行ファイルの終了を明示的に待ち、終了コードと今回の結果ファイルを確認します。

`Test-Repository.ps1` はGitで追跡するファイルの配置・拡張子、バイナリ混入、固定設定の指紋、PowerShell構文、代表的な秘密鍵・GitHubトークン・ルームコードの形式を検査します。任意の秘密情報や過去のGit履歴を網羅する検査ではありません。コミット前には `git diff --cached` も確認してください。新しいソース形式を追加するときは、検査の許可リストも明示的に更新します。

Windows CIは同じ検査と `build.ps1 -SelfContained`、`--self-test` を実行します。原作の起動、注入処理、描画、全ステージ、別PC間のオンライン動作はCIの対象外です。実ゲーム検証は所有する対応版を専用コピーへ準備して行い、使用したバイナリ指紋と実測結果を [検証記録](../verification/RESULTS.md) に残します。

CIは `contents: read` のみを使用し、認証情報をcheckout後に保持せず、リリースを自動公開しません。Actionsは確認した公式リリースの完全なコミットSHAで固定しています。更新時は [checkout](https://github.com/actions/checkout/releases)、[setup-dotnet](https://github.com/actions/setup-dotnet/releases)、[GitHubのセキュリティ指針](https://docs.github.com/en/actions/reference/security/secure-use) を確認してください。Windowsランナーのコンパイラー構成は [公式イメージ一覧](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md) で確認できます。

## リリース

作業中の次版は `0.2.0`、通信番号4、協力ルール番号4。機体選択の開始ハンドシェイクとPC別設定の分離を追加している。公開済みの0.1.1配布物・タグは変更しない。

実ゲームの自動検証前には `build.ps1 -NativeOnly -SmokeTest` も実行する。音声バッファの無音化とウィンドウの非表示を担当するテスト専用DLLをビルドし、音声デバイスを開かないガード自己テストを実行する。テストは用途別の `game-test-*` コピーまたは隔離セッションだけで実行し、フォーカス・入力・クリップボード・Windows音量を変更しない。詳細と描画検証の制約は[無音・非干渉テスト](quiet-testing.md)を参照。

初回の `v0.1.0` は Rules 2、修正版 `v0.1.1` は Rules 3 の **Pre-release** です。製品のバージョン番号と協力プレイのルール番号は別の番号です。通信・ルール・設定の互換性が変わる修正では、対応するバージョン判定と検証も更新します。

リリース対象のコミットについてCIと変更に応じた実ゲーム検証を完了し、対応版・実施済み検証・既知の制約をリリースノートへ記録します。リリースでは `scripts/Package.ps1` でZIPを作成し、`scripts/verify_package.py --archive <ZIP> --out build/package-check.json` でソースとの内容一致・構造・ランタイムを検査する。原作データやsmoke DLLが混入していないこと、展開後の起動、SHA-256を検証します。初回は既に検証したZIPを同じバイト列のまま添付し、CIの再ビルド出力へ差し替えません。

以下は検証済みZIP、`release/SHA256SUMS-v0.1.1.txt`、実測内容を書いた `docs/releases/v0.1.1.md`、そのZIPに対応するコミットを用意した後の操作例です。タグは必ず検証対象コミットへ付けます。公開済みのタグや添付ZIPを上書きせず、修正は新しいバージョンとして出します。

```powershell
$tag = 'v0.1.1'
$validatedCommit = '<検証対象の完全なコミットSHA>'
$zip = 'release/TH12COOP-v0_1_1-Windows.zip'
$notes = "docs/releases/$tag.md"
Get-FileHash -LiteralPath $zip -Algorithm SHA256
Get-Content release/SHA256SUMS-v0.1.1.txt
# 指紋と検証記録、ZIPとリリースノートを確認してから実行。
git tag -a $tag $validatedCommit -m "TH12 CO-OP $tag (Rules 3)"
git push origin $tag
gh release create $tag $zip release/SHA256SUMS-v0.1.1.txt --repo moyu1254/th12co-op --verify-tag --prerelease --latest=false --title "TH12 CO-OP $tag" --notes-file $notes
gh release view $tag --repo moyu1254/th12co-op
```

`--verify-tag` はリモートにタグがなければ作成を中止します。操作の詳細は [GitHub CLIのrelease create](https://cli.github.com/manual/gh_release_create) を参照してください。初回以降は例のタグを次のバージョンへ変更し、実際の安定性・検証範囲に合わせてPre-releaseの扱いを決めます。
