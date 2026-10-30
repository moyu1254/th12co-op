# 非干渉ガードの独立レビュー

2026-10-30。実ゲームを起動せず、通常版1.00a・通常版1.00b・Steam版1.00bの手元の既知EXE、`src/injector/quiet_test.cpp`、C#/Python起動経路を確認した。実ゲームの結果とは別の静的・自己テスト記録。

## 確認と修正

* 3版の直接インポートに、`SetCursorPos`・`ClipCursor`・`SendInput`・クリップボード操作はない。音声の直接インポートは`DSOUND.dll`のordinal 11。WinMMはジョイスティックと時計の関数。COM生成箇所はShellLinkで、別の音声生成経路ではない。
* 1.00aの`0x450C5E`–`0x450C86`は`SendMessageA(WM_SYSCOMMAND)`を使い、最小化後に復元する。`ShowWindow`だけのガードでは防げないため、`SafeMessage`でシステムコマンドを遮断し、別プロセスのウィンドウ宛てメッセージも拒否した。
* 静的リンクされたCRTは`0x482F23`以降でUSER32を開き、`GetProcAddress("MessageBoxA")`を使う。直接インポートだけのガードを迂回するため、動的解決も`GuardProc`で保護した。
* エラーUIはゲームのMessageBoxに加え、ゲームプロセス内の`SetErrorMode`と`WerSetFlags(WER_FAULT_REPORTING_NO_UI)`で抑止する。Windows全体の設定を変更する処理はない。
* DirectSoundは`DSSCL_NORMAL`を使い、プライマリーバッファの音量・形式・再生・停止・位置・パンなどの変更を実体へ転送しない。セカンダリーバッファを作成時と再生直前に`DSBVOLUME_MIN`へ設定し、設定失敗時は再生しない。スピーカー構成も変更しない。
* ウィンドウは不可視かつ`WS_EX_NOACTIVATE`で作成する。Direct3D9はウィンドウモードだけを受け付け、`D3DCREATE_NOWINDOWCHANGES`を追加する。Microsoftの仕様では、このフラグはDirect3Dがフォーカスウィンドウを変更することを禁止する。[D3DCREATE](https://learn.microsoft.com/en-us/windows/win32/direct3d9/d3dcreate)
* 原作DirectInputのキーボードは`NONEXCLUSIVE | FOREGROUND | NOWINKEY`、ジョイスティックは`NONEXCLUSIVE | BACKGROUND`。キーボード取得はゲームが前面でない間は成功せず、ジョイスティックも排他取得しない。
* C#/Pythonの起動は停止状態で作成し、所有Jobへ登録してからガードを注入する。注入失敗時はゲームを再開せず終了する。デスクトップ切り替えや入力注入は使わない。

## 独立自己テスト

以下をレビュー担当が実行し、終了コード0とPASSを確認した。原作ゲームは起動していない。自己テストの音声オブジェクトは偽物で、音声出力は作らない。検証用ウィンドウは不可視で作成・破棄する。

```powershell
.\build\quiet-guard-self-test.exe
```

```text
quiet guard: primary untouched, secondary mute/fail-closed, hidden/noactivate window, bounded cursor calls: PASS
```

このガードは既知の原作コードの出力経路を対象とする。ガード読み込み前のWindowsローダー、Steamの起動スタブ、外部グラフィックスドライバー・オーバーレイが独自に生成するUIまで、原作EXEのインポート差し替えだけで完全に網羅したと扱わない。非表示でのDirect3D実行、音量固定、エラーUIの遮断による制約は実ゲーム検証にも併記する。
