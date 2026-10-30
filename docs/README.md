# ドキュメント

## 利用・仕様

| 資料 | 内容 |
|---|---|
| [起動とオンライン接続](../README.md) | 対応環境、導入、操作、接続 |
| [協力ルール](cooperative-rules.md) | 共有資源、被弾、復帰、標的、回収 |
| [協力ショット補正](cooperative-damage.md) | 目標倍率、同一フレーム集計、ボムの分離 |
| [入力設定](local-input.md) | キーボード、XInput、Windows joystick |
| [PC別設定](local-configuration.md) | 画面・音量と共通進行設定の分離 |
| [検証結果](../verification/RESULTS.md) | バージョン別の実測結果と未検証範囲 |

## 開発

| 資料 | 内容 |
|---|---|
| [開発ガイド](development.md) | 取得、ビルド、自己テスト、配布 |
| [開発への参加](../CONTRIBUTING.md) | 不具合報告と変更の検証 |
| [エンジン接続](native-engine.md) | 原作とnative DLLの接続 |
| [個別機体](independent-characters.md) | 機体別の生成・更新・解放 |
| [Native IPC](native-ipc.md) | 共有メモリとフレーム確定 |
| [通信設計](network-security-design.md) | TLS、招待、同期、信頼境界 |
| [脅威モデル](TH12CO-OP-threat-model.md) | 評価時点の前提と残る制約 |
| [バージョン検証](version-layout-audit.md) | 対応EXEの指紋とレイアウト |
| [実ゲームテスト](quiet-testing.md) | 隔離コピー、無音、所有プロセス管理 |
| [権利と参照資料](../NOTICE.md) | 原作、ランタイム、照合資料 |

## リリースノート

[v0.3.0](releases/v0.3.0.md) · [v0.2.0](releases/v0.2.0.md) · [v0.1.1](releases/v0.1.1.md) · [v0.1.0](releases/v0.1.0.md)

## 過去の監査記録

以下は修正の根拠と当時の検証範囲を残した記録です。現在の仕様・検証状態は上記の仕様書とバージョン別検証結果を参照してください。文中の `research/` と `artifacts/` は非同梱のローカル作業記録です。

- [資源と寿命](audit-resources.md)、[資源監査の追補](audit-resources-followup.md)
- [アイテムと進行](audit-items-progression.md)、[アイテム再監査](audit-items-recheck.md)
- [標的選択](audit-targeting.md)
- [弾の同期digest](audit-projectile-digest.md)
- [managed層](audit-managed.md)
- [追加監査](audit-followup.md)
