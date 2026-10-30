# 開発への参加

開発先は [moyu1254/th12co-op](https://github.com/moyu1254/th12co-op) です。ビルドとリリースの詳細は [開発ガイド](docs/development.md)、実装資料は [ドキュメント一覧](docs/README.md) を参照してください。

## 不具合報告

[Issues](https://github.com/moyu1254/th12co-op/issues) に、MODのバージョン、原作の版、Windowsの版、オフライン／オンライン、再現手順と期待する動作を記載してください。同期の問題では機体・装備・モード・開始面、同一PC／LAN／WANのどこで発生したかも記載してください。

ルームコード、参加鍵、IPアドレス、ユーザー名を含むパス、原作データ、セーブ、リプレイ、メモリーダンプは添付しないでください。ログは必要な範囲だけを抜粋し、個人情報とセッション情報を除去してください。

## 変更と検証

根本原因を直す小さな変更にまとめ、変更理由と検証方法をPull Requestに記載してください。ソースの提供条件は [MIT License](LICENSE) です。

```powershell
.\scripts\Test-Repository.ps1
.\build.ps1 -SelfContained
.\scripts\Test-Repository.ps1 -RunSelfTest
```

原作データが不要なテストと、実ゲームを使用したテストを区別してください。ゲーム接続・入力・同期・ルールを変更した場合は、所有する対応版の隔離コピーで必要な実ゲーム検証を行い、使用したバイナリのSHA-256、結果、未検証範囲を `verification/` に記録してください。無音・非干渉での実行方法は [実ゲームテスト](docs/quiet-testing.md) を参照してください。

`game*`、`sessions/`、`research/`、`artifacts/`、`build/`、`dist/`、`release/` はローカル作業用です。原作データ・認証情報・生の調査ダンプはコミットしません。検証記録は公開できる数値要約と指紋に限定し、過去の失敗や旧バージョンの証跡を新版の成功結果へ書き換えないでください。
