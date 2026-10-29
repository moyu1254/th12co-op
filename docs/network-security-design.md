# TH12 CO-OP 通信実装ガイド

更新日: 2026-10-29。非公開招待の2人が、それぞれのWindows 11 PCで正規のTH12を実行する。対象は検証した通常版1.00a・通常版1.00b・Steam版1.00bの3本体。入力を同期し、描画と音声は各PCで処理する。脅威と残るリスクは[脅威モデル](TH12CO-OP-threat-model.md)、実測した試験範囲は[検証結果](../verification/RESULTS.md)に記録する。

## 通信と認証

Protocol.csは.NETのSslStreamでTLS 1.3を必須にする。アプリの通信版は3、協力プレイルール版は3、ローカルIPCの構造体版は1で、それぞれ別に検証する。ホストはECDSA P-256の一時証明書と256-bitのランダム参加鍵を作成する。T12L3招待には証明書全体のSHA-256と参加鍵を格納し、接続先IP・ポートは別に指定する。ゲストは証明書のハッシュを固定時間で照合し、ホストはTLS内で参加鍵を固定時間で検証する。成功後に128-bitのセッションIDを発行する。終了・中止・失敗後は部屋の証明書と参加鍵を更新する。旧版の招待・通信版・ルール版は受理しない。

TLSが鍵交換、方向別の鍵、暗号文の完全性を担当する。独自AES-GCMレコード層や0-RTTのゲーム開始処理は使用しない。自己署名証明書は招待のpinと一致する場合だけ受理する。[TLS 1.3](https://www.rfc-editor.org/rfc/rfc8446.html)、[TLS運用指針](https://www.rfc-editor.org/rfc/rfc9325.html)、[SslStreamの公式指針](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-best-practices)。

SchannelでTLS 1.3を使用するため、オンラインの対象はWindows 11とする。平文への切替えはない。[Schannel対応表](https://learn.microsoft.com/en-us/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp-)。この環境ではSchannelがEphemeralKeySetの鍵を取得できなかったため、証明書はUserKeySetを指定し、PersistKeySetを指定せずロードする。PFXバッファをゼロ化し、部屋終了時に証明書を破棄する。現在ユーザーのWindows鍵領域を使用し、信頼済み証明書へ登録する処理はない。秘密鍵がメモリだけに存在するとは保証しない。[鍵格納フラグ](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509keystorageflags?view=net-10.0)。

## 実装上限

| 対象 | 制約 | 実装 |
|---|---|---|
| 認証前 | 同時8件、TLS＋参加認証4秒、認証済み待機1件 | OnlineSession.AcceptGuest |
| クライアント接続 | TCP＋TLS＋認証応答で10秒 | SecureChannel.Connect |
| 本文 | 1〜4096 bytes。範囲外は本文配列の確保前に拒否 | SecureChannel.Receive |
| 通信期限 | 送信5秒、長さ＋本文の受信40秒。送信1担当・受信1担当 | SecureChannel |
| 入力／digest | 20／44 bytes、役割・版・session・連続frame・入力bitを検証 | LockstepPeer.AcceptRecord |
| 保存窓 | 先行120フレーム、入力とdigestの各辞書120件まで | LockstepPeer |
| 頻度 | 512レコード/秒・バースト1024、pingは4件/秒・バースト8 | AcceptRecord |
| 遅延 | 開始前に1〜12フレームで合意 | SessionManifest |
| IPC | 固定12544 bytes、入力・digest各256スロット | NetBridge、lockstep.h |
| 通常進行 | 入力待ち・engine要求待ち35秒、native barrier40秒。loader待ち30秒＋通信余裕 | LockstepPeer、NetBridge、lockstep.h |
| 起動／終了 | 準備全体120秒、native最初の待機125秒、所有ゲームの終了待ち5秒 | LockstepPeer.Run、GameSession |

TCPの分割・結合を前提にReadExactlyAsyncで読む。不明種別、過大本文、重複・過去・過剰に未来のframeを拒否する。pingで必要入力の期限を延長しない。上限はアプリ内の消費を制限するもので、回線DDoSや悪意ある参加者自身の切断を防ぐ保証ではない。

## 開始合意とローカル実行

OnlineProfile.ReadはローカルEXE・DAT・BGM・MOD DLL・固定オンライン設定をSHA-256で識別し、既知EXEと対応するDAT/BGMの組合せを検証する。既知本体は通常版1.00a・通常版1.00b・Steam版1.00bの3つに限定する。manifestにはプロトコル版、ルール版、入力遅延、乱数seed、セッションID、既知build ID、資産と依存DLLの識別値を含め、ゲストはmanifest全体のハッシュへ応答する。OnlineProfile.ValidateRulesはルール版3以外をセッション準備前に拒否する。NetBridgeはIPC offset76へ3を書き、native lockstep.Initializeも3以外をフック設置前に拒否する。機体と難易度は同期したゲームメニューで選択する。

通常版1.00bとSteam版1.00bは、正常起動後のゲームコードとデータが一致するため混在を許可する。比較前に双方のbuild IDとEXE hashを既知プロファイルへ照合し、そのうえで同じエンジン版・DAT・BGM・MOD DLL・設定・依存関係の識別値を要求する。build IDを名乗るだけで任意のEXEを許可する処理ではない。1.00aと1.00bの混在は拒否する。根拠は[本体比較](version-layout-audit.md)に記録する。

依存DLLのローカル許可リストは`d3dx9_40.dll`。存在する場合はそのhash、存在しない場合は`system`という識別子を依存関係digestへ含め、相手からファイル名を受け取らない。`system`同士の一致は両PCにインストールされたシステムDLL全体の一致を証明しない。

作業領域はローカルのゲーム配置を基準にsessions/セッションID/hostまたはguestへ作る。IDは固定長バイナリの16進表現で、相手が送った任意パスを解釈しない。EXE・DAT・BGM・許可されたローカル依存DLLはローカルのハードリンクまたはコピーを使用し、スコア・リプレイ・オフライン設定を持ち込まない。Steam用の作業領域には固定のApp ID `1100160`を使用し、正常なSteam起動と所有確認を保持する。相手からゲーム資産やDLLを取得する処理はない。

オフライン・オンラインともゲームをCREATE_SUSPENDEDで生成する。ローカルの16バイト起動mapping `Local\TH12CoopBoot_<pid>` にmagic `0x42323154`、版1、CreateProcessが返したprimary thread ID、early flag1を置く。coop_boot.Initializeは内容とスレッドの所属プロセスを照合する。既存のネットワーク診断には、同じearly起動を使う有効なNet IPC offset92からの互換経路がある。通常のオフライン起動はBoot16なしでは拒否する。これは起動順序の契約であり、同一ユーザーの悪意あるプロセスを隔離する認証ではない。

injectorは既知entryでゲームの実行を待機させ、Windowsのimport loadingが完了した状態でローカルDLLを読み込む。通常版はprimary thread再開前にgame hookを設置する。Steam版はstatus5の起動待受を設置してから正常なSteam起動を再開し、最初のseed呼出しでのみgame hookを検証・設置する。この呼出しは既知の戻り先と、coop_bootが確認したprimary thread IDの一致を要求する。元のtimeGetTime importを復元してからseedを返し、設置後にのみdevice-readyを公開する。暗号化コードの解除や認証の迂回をMODが行う処理はない。

パッチ登録は全対象の命令署名と重複範囲を確認してから適用する。割当失敗は書換え前に終了し、書換え途中の保護変更失敗では適用済み命令の復元を試みる。OSの保護変更自体が失敗する場合まで完全な巻戻しを保証しない。通常版はprimary threadを再開せず、Steam版は起動待受の失敗ループでゲーム進行を止め、ランチャーが自分で生成したプロセスを終了する。

両者の準備完了後、nativeの入力境界で同じframeの両入力を確定する。NetBridge.Commitは直前commit＋1、native要求、消費済みframe＋1のすべてと一致する場合だけ書く。次frameのcommit前に直前digestを読むため、現在の単一writer構成でリング周回が読取りを追い越さない。

入力欠落時はゲームの進行を待機する。開始時の合意済み遅延分を除き、欠落入力をゼロや過去入力で補って続行しない。切断・中断・不一致ではabortを通知し、ランチャーが起動したゲームだけを終了する。開始Task、通信Task、共有メモリの寿命は終了処理で回収する。セッション作業領域と診断ログはローカルに残り、原本へ書き戻さない。

OwnedGameJobは停止中のゲームを、KILL_ON_JOB_CLOSEを設定した無名Job Objectへ注入前に登録する。ランチャーだけが非継承handleを保持し、通常終了時もランチャー自体の異常終了時もhandle閉鎖で所有ゲームを終了させる設計。SILENT_BREAKAWAY_OKにより、ゲームが生成したSteam補助プロセス等をこのJobへ自動所属させない。登録やnested jobの制約で失敗した場合は停止中に終了し、所有権なしで再開しない。[Jobの制限](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)、[プロセス登録とnested job](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject)。`--owner-exit-test --game <準備済みコピー>` は起動したPIDを記録し、明示的なDisposeを呼ばずにランチャーを終了する。ゲームが実際に終了したかは外部runnerが確認するため、CLIの起動記録だけを合格結果としない。実測結果は[検証結果](../verification/RESULTS.md)を参照する。

通信から受理するのは開始合意、入力、数値digest、ping、列挙値の終了理由。任意のパス、URL、DLL、シェルコマンド、PID、メモリアドレス、画像、音声、メモリダンプを実行・復元するメッセージはない。

## 状態digestの意味

src/native/lockstep.hのdigestは、検証した数値フィールドを集約するFNV-1a 64-bitの診断値。TLSの改ざん検出とは別で、全状態の証明やアンチチートではない。対象には共有ゲーム値・乱数・ランク、自機・自機弾・有効なdamage source、ボムとスペルの確認済み共通値、敵の座標・変数・HP・一部ECL状態、敵弾、アイテム、メニュー・ポーズ、検証したレーザーの共通状態・曲線軌跡を含む。

ルール版3は各actorの復帰権、全滅発行状態、ボム所有者、アイテム・標的・衝突などのMOD側の保持状態も集約する。霊夢Aはnative ANMリストを元の排他区間で走査し、現世代の対象emitter/beamだけから所有者ID、確認済み座標・タイマー、actor内のdamage source番号を採用する。無関係な演出の個数やメモリアドレスを比較しない。採用オフセットと上限はコードを正とし、[資源監査](audit-resources.md)に所有者と寿命の根拠を記録する。

構造体全体、ポインター、padding、未初期化bitを比較しない。Timerの速度ポインターや自機弾のSHTポインターを除外する。ECLスタックには戻り先ポインターがあり、生の比較は不適切。ECLのins18/19フラグはbit 0だけが初期化・更新されるため、上位31bitをdigestから除外する。レーザーの共通領域は対応EXEのコンストラクターによる全体ゼロ初期化を確認した。

ルール3ではprimary/async ECLの命令位置と初期化済みscalar、RA beam履歴の確認済み`+2A0..+363`、UFO・ベントラー・報酬、弾Exタイマー/数値/補間値、レーザー命令位置を追加した。raw ECL stack、一部の弾寿命/グレイズ/レーザー内部/ホーミング参照などは未収録で、全状態の正規化は未完了。詳しくは[追加監査](audit-followup.md)と[弾の残存範囲](audit-projectile-digest.md)を参照する。現在の攻撃形状は別途damage sourceに含むが、将来の挙動まで同一である証明にはならない。これらは検出範囲の制限であり、それだけで実ゲームの同期不良が発生したという報告ではない。未採用状態の差は後から検出される可能性がある。不一致時はframeと数値digestをローカルに記録して停止し、相手の状態を取り込んでメモリを書き換える回復は行わない。同一PCでの試験を別PC・WAN・全ステージの確認へ拡張せず、実行済み範囲は[検証結果](../verification/RESULTS.md)を参照する。

## ランタイムと配布

ランチャーはnet10.0-windows、自己完結配布は.NET 10.0.14／SDK 10.0.403を基準にする。2026-10-29時点の公式metadataと照合し、SDK ZIPのSHA-512を検証してresearch/dotnet10へ隔離配置した。グローバルSDK・環境変数は変更していない。10系のLTS終了日は2028-11-14。[公式リリース情報](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)、[サポートポリシー](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。自己完結配布にはランタイムが含まれるため、MOD配布物も継続更新する。

peer間のハッシュ一致は配布元の真正性を証明しない。ローカルDLL・ランチャーと配布経路は信頼する必要がある。DLLは固定のローカル絶対パスから読み、相手のmanifestでロード先を変えない。[DLL読込み対策](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-security)。固定サイズIPCとPID名は同一ユーザーのマルウェアを隔離するサンドボックスではない。

## 外部中継を追加する場合

現在は直接TCPのみ。LAN、到達可能なIPv6、利用者が用意したVPNやポート転送を使う。暗号化とNAT越えは別の問題で、直接接続では相手にIPが分かる。外部サービスの作成・契約・デプロイ・ルーター変更は行っていない。

将来の最小構成は、サポート中のASP.NET Coreで2接続を結ぶWSSサービス。公開一覧・DB・資産保存・任意宛先転送を持たせず、短命の部屋、host/guest別権限、接続数・転送量・TTLの上限を設ける。秘密をURLやログに含めない。中継を信用しない場合はWSSの順序付きStream内に同じpeer間TLSを通し、外側HTTPSだけをE2E暗号化と呼ばない。

外側WSSの断片合算上限と内側アプリ4KiB上限を分け、無限キュー・圧縮を禁止する。各接続で送信1つ・受信1つを所有する。[WebSocketの制約](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocket?view=net-10.0)。自動NAT探索を追加するなら既存の[ICE](https://www.rfc-editor.org/rfc/rfc8445.html)・[TURN](https://www.rfc-editor.org/rfc/rfc8656.html)を用い、運用者・ログ保持・パッチ担当・料金上限を別途決める。これは将来設計で、今回の実装・検証範囲には含めない。

