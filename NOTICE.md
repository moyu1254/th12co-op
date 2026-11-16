# 権利と参照資料

TH12 CO-OPが作成したソースコードと付属ドキュメントは [MIT License](LICENSE) で提供します。下記の第三者コンポーネントには各ライセンスが適用されます。

## 原作

「東方星蓮船 ～ Undefined Fantastic Object.」および原作のプログラム・画像・音声などの権利は上海アリス幻樂団 / ZUNに帰属します。本プロジェクトは非公式のMODです。利用には各プレイヤーが所有する対応版の原作が必要です。原作の実行ファイル、データ、セーブ、リプレイはソースや配布ZIPに含めません。原作は本プロジェクトのMITライセンスの対象ではありません。

## 同梱ランタイム

部屋番号の UDP 接続には [Keryx 0.4.0](https://github.com/Proxeno/keryx/tree/3601d71bb5758ed98acea07bb07d5e4df886340b) の Core、STUN、ICE、および依存 DLL の TURN を同梱します。Copyright (c) Keryx contributors。ライセンスは [Apache License 2.0](licenses/Keryx-LICENSE.txt) です。配布 ZIP では `dist/licenses/` にも収録します。

ICE は [上流の `src/Keryx.Ice/*.cs`](https://github.com/Proxeno/keryx/tree/3601d71bb5758ed98acea07bb07d5e4df886340b/src/Keryx.Ice) を `src/third-party/Keryx.Ice/` の同名ファイルに取り込んでビルドします。TH12 CO-OP は `IceAgent.cs` と `IceAgentOptions.cs` に受信チェックの送信元制限、学習候補の上限、データグラムの送信元通知を追加しました。その他の C# ファイルは上流と同一です。`Keryx.Ice.csproj` はこのプロジェクト用で、Core・STUN・TURN は 0.4.0 の NuGet パッケージを使用します。更新時は上記コミットと更新先の `src/Keryx.Ice/` の差分を確認し、ローカルの変更を照合してからパッケージの固定版と参照コミットを更新し、UDP 経路の自己テストを実行します。

自己完結版ZIPの.NETランタイムには、それぞれのライセンスが適用されます。ビルド時に実際に使用したランタイムパッケージからライセンス・第三者表記を `dist/licenses/` へコピーし、配布ZIPにも同梱します。

## 照合に使用した資料

- [exphp-share/th-re-data](https://github.com/exphp-share/th-re-data)：関数名と構造体の照合。
- [touhouworldcup/thprac](https://github.com/touhouworldcup/thprac)：原作の関数・構造の照合。
- [TH12 HFR development notes](https://github.com/vittorioromeo/th12_hfr/blob/45a7ecc4daedc3494bd7ef9211eb1c87073c5694/docs/games/TH12_DEVNOTES.md)：エンジン接続の調査資料。

参照先のプロジェクトや原作へ、このリポジトリのライセンスを適用するものではありません。各対応版のアドレス・命令は所有する実行ファイルで照合しています。詳細は [エンジン接続](docs/native-engine.md) と [バージョン検証](docs/version-layout-audit.md) を参照してください。
