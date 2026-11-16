# 部屋番号で接続する（開発版・Phase 2）

ホストとゲストは同じ合流サーバーを選び、6桁の番号で合流します。ゲストの参加時に両者が UDP の接続候補を交換し、ICE の接続性チェックで選んだ経路に直接 QUIC/TLS 1.3 を張ります。ゲームを始める前に両方の画面の照合コードを別経路で確認してください。[オンラインの手順](../README.md#オンライン)・[通信ガイド](network-security-design.md)も参照してください。

## ランチャーの設定

運用者から信頼できる経路で HTTPS の接続先を取得し、両方の PC で `TH12COOP_RENDEZVOUS_URL` を設定してから起動します。ドメイン名と任意のポートまでを指定します。OS が信頼し、その名前に対して有効な証明書が必要です。以下の `rooms.example.org` は運用者のホスト名に置き換えてください。

```powershell
$env:TH12COOP_RENDEZVOUS_URL = 'https://rooms.example.org:8443'
.\Start.cmd
```

ホストは番号を発行して待ちます。ゲストが番号を入力すると、STUN で集めた候補を登録し、招待と受取用の鍵を取得します。ホストはこの時点で自分の候補を集めて応答します。IPv4 のホストでは UPnP で UDP のポート転送も試みます。Windows ファイアウォールの許可は利用者が設定します。

UDP が遮断されている場合や、相手によって NAT の外側ポートが変わり接続性チェックで経路が成立しない場合は、LAN・VPN などで手入力の直接 TCP 接続を利用できます。公開中継を使う Phase 3 は別の作業です。別回線で実測した範囲は[検証記録](../verification/RESULTS.md)に従ってください。

## サーバーを運用する

この構成は小規模グループ向けです。運用者が DNS、証明書、OS/.NET と STUN サービスの更新、回線・費用上限、アクセス制限を管理します。プロジェクト提供の公開サービス・クラウド契約はなく、プロジェクトのホスティング費用枠は0円です。

1. TLS 1.3 対応ホストに .NET 10 SDK を用意し、`dotnet publish src/rendezvous -c Release -o build/rendezvous` で HTTPS サーバーをビルドします。実行先に ASP.NET Core 10 ランタイムが必要です。
2. 接続名に対するサーバー証明書・秘密鍵・必要な中間証明書を PFX にまとめ、サービス用アカウントだけが読める場所に配置します。`TH12_RENDEZVOUS_PFX` に絶対パスを指定し、必要に応じて `TH12_RENDEZVOUS_PFX_PASSWORD` をサービスのシークレット設定から渡します。`dotnet build/rendezvous/TH12Coop.Rendezvous.dll` で起動します。HTTPS は既定で TCP 8443 です。`TH12_RENDEZVOUS_PORT` で変更できます。
3. 同じ DNS 名の UDP 3478 に STUN Binding サービスを別途用意します。例えば coturn では `stun-only` モードを使い、使用するアドレスで UDP を受け付けます。IPv6 と IPv4 の両方を提供する場合は AAAA・A を設定し、HTTPS と STUN の双方が各ファミリーで到達可能になるようにします。HTTPS サーバーのプロセスは STUN を提供しません。[coturn の設定例](https://github.com/coturn/coturn/blob/master/examples/etc/turnserver.conf)・[STUN の仕様](https://www.rfc-editor.org/rfc/rfc8489)。
4. Kestrel が直接 HTTPS を終端します。TLS 終端プロキシや CDN を置くと候補の送信元を証明できません。部屋と IP 証明は単一プロセスで管理します。HTTPS と STUN の両方で本文・番号・接続元のアクセスログ、APM、ダンプの保存を無効にし、STUN にも運用環境側の受信・帯域制限を設定してください。[Kestrel のエンドポイント](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)。

各端末は、候補がある IP バージョンごとに HTTPS 接続を張り、そのソケットの接続元 IP に対する短期の証明トークンを取得します。サーバーがそのトークンで確認したアドレスだけを相手に渡します。STUN と HTTPS の出口 IP が異なる候補は登録できません。DNS 名・LAN 内・リンクローカル・予約済みの宛先は受理しません。ループバックの HTTPS を明示設定するローカル試験は例外です。証明対象は IP で、UDP ポートは ICE で実際の到達性を検査します。

## HTTP インターフェースと保持期間

次の固定パスへ JSON を HTTPS POST します。応答には `Cache-Control: no-store` を付けます。招待・ICE パスワード・トークンは本文にのみ含めます。ゲーム通信とゲーム資産はこのサーバーを通りません。

| `/v2/rooms/` の下のパス | 送る値 | 成功時 |
|---|---|---|
| `create` | `invite`（T12L6） | 200、`number`（6桁）、`ownerToken` |
| `proof` | 空のオブジェクト | 200、観測した `address` と `token` |
| `join` | `number`, `offer`（証明付き候補） | 200、`invite`, `guestToken`。番号を消費 |
| `offer` | `number`, `ownerToken` | 200、ゲストの候補。参加待ちに使用 |
| `answer` | `number`, `ownerToken`, `ice`（証明付き候補） | 204、ゲスト用の候補を一度だけ発行 |
| `receive` | `number`, `guestToken` | 200、ホストの候補を一度だけ返す |
| `close` | `number`, `ownerToken` | 204、所有者が取り消す。不在・権限不一致も同じ応答 |

証明付き候補は `description` と `proofs` から成ります。`description` の項目は `ufrag`（4～256文字）、`password`（22～256文字）、`candidates`（1～4件）です。候補は数値 IP の `address` と UDP の `port`（1024～65535）だけを持ちます。`proofs` は IPv4・IPv6 の証明トークンを最大2件含み、相手には渡しません。所有者用・ゲスト用・IP 証明トークンはそれぞれ独立した256-bitの乱数です。

部屋の期限は作成要求の受付から10分です。参加を受け付けた時点で招待を部屋から外し、候補の配達後または取消後には番号だけを元の期限まで予約します。取得後に接続が失敗した場合は新しい部屋を作成してください。各役割で候補を待てる HTTPS 接続は一つです。取消・期限切れで待受を解放し、不在・受取済み・無効な権限には404を返します。IP 証明と頻度制限の受付記録も、それぞれの受付から10分で失効します。プロセス再起動でメモリ内の予約・証明は失効します。OS のページファイルなどの物理消去は保証しません。

次の値は操作ごとの直近10分の受付件数です。本文を読む前に照合し、上限を超えた要求には429を返します。

| 集計範囲 | create | join | proof | offer | answer | receive | close |
|---|---:|---:|---:|---:|---:|---:|---:|
| IPv4 アドレスまたは IPv6 /64 | 4 | 6 | 24 | 12 | 12 | 12 | 12 |
| IPv6 /56 | 8 | 12 | 48 | 24 | 24 | 24 | 24 |
| IPv6 /48 | 12 | 24 | 72 | 36 | 36 | 36 | 36 |
| サーバー全体 | 48 | 120 | 384 | 96 | 96 | 120 | 96 |

Kestrel は最大64接続、本文4096 bytes、TLS ハンドシェイクと要求本文の受信は各5秒です。多数の送信元による利用枠の消費や回線混雑によって、期限内でも接続に失敗することがあります。
