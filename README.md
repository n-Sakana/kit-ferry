# Ferry

> **このリポジトリは fork です。**
> 上流の [bashalarmistalt/decimen-optical-transfer](https://github.com/bashalarmistalt/decimen-optical-transfer)
> （v0.3.0 / MIT / Copyright (c) 2026 Evan Crawley (Bash Alarmist)）を基礎に、
> Ferry 固有の機能を追加しています。上流のワイヤーフォーマットには手を入れていません。

フォルダを、ネットワークを通さず画面とカメラの間で運ぶためのアプリです。光学転送、Office 文書の Markdown 化、Excel を起動しない VBA 抽出を一つの画面から使えます。

画面は C# のローカル HTTP サーバが配信します。Windows デスクトップでは WPF の窓に
WebView2 で表示し、モバイルでは同じ画面を PWA として使います。

## 2026-09-08 バグ修正版

修正内容・測定結果・未確認事項は [`BUGFIX_REPORT.md`](BUGFIX_REPORT.md)、
再現テストの手順は [`tests/README.md`](tests/README.md) にあります。
QR の既定値は認識安定性を重視して **1000 bytes / 30 fps / 誤り訂正 L** に変更しています。
QR の追跡・全画面再探索、一時的な通信失敗時の再試行、QR 配信のビット圧縮とまとめ読みを追加しました。

この ZIP はソース配布です。起動スクリプト・必要な同梱 DLL・WASM は含みますが、
古いビルド出力 `bin/`・`obj/`・`artifacts/` と `.git/` は含めていません。
今回の環境では **C# の再コンパイル、Windows 上の起動、実カメラ転送を未検証**です。
JavaScript の自動テストと Chromium 上の合成画像・仮想カメラ試験の結果を同梱しています。

## 起動（配布の本線）

Windows では `ferry.vbs` をダブルクリックします。黒いコンソールを出さずに Windows 標準の
Windows PowerShell 5.1 を起動し、C# ソースをその場でコンパイルして WPF + WebView2 の窓を
開きます。WebView2 Runtime が必要です。起動前に失敗した場合は
`%LOCALAPPDATA%\Ferry\logs` のログを案内します。

起動ログを見ながら使う場合は `ferry.cmd` を実行します。非 0 終了時にはコンソールが残ります。
生成された DLL はユーザー別キャッシュへ保存され、C# ソースが変わったときだけ作り直します。

Linux 用デスクトップ画面は未実装です。PowerShell 7 では HTTP サーバだけを起動できます。

```powershell
pwsh -NoProfile -File ./ferry.ps1 --no-browser
```

Windows で WPF の窓を出さず HTTP サーバだけを起動する場合は `--no-browser`、開始画面を
指定する場合は `--mode optical|markdown|vba` を使います。

## ソースファイルの Markdown 化

`.cs`・`.cpp`・`.rs` などのソース、未知の拡張子のテキスト、`Makefile`・`Dockerfile`・`LICENSE`
も対象です。フォルダまたはファイルを選び、同じ「書き出す」ボタンから1枚の Markdown にまとめます。
コードには言語名付きのコードフェンスを付け、言語が分からない場合は言語名を省きます。
従来の Markdown・プレーンテキスト・CSV 等と PDF・Office の出力形式は維持します。

一覧では先頭 8 KiB を復号してテキストか判定し、書き出し時には全文を再検査します。
BOM 付き UTF-8 / UTF-16、BOM なし UTF-8、CP932 を読み取り、不正なバイト列や NUL・制御文字
（タブ・改行・改ページを除く）があれば除外します。PNG・実行ファイル・書庫などをテキストとして
書き出しません。フォルダから読むとき、隠しファイルは Markdown の候補から除外します。
Windows で Hidden 属性のないドットファイルも本文へ取り込まず、名前だけを「対象外」に残します。
隠しディレクトリの探索範囲は従来どおりです。
`.gitignore` 等も、ファイルを明示選択した場合はテキストとして変換できます。

ソースの読み取りに不要な `.svg`・`.trx`・`.lock`・`.sha256`・`.sha512`・`.metadata`・`.map`
は内容を取り込みません。退避用の `.bak`・`.bak-…`・`.bak_…` も除外します。
`.json`・`.jsonl`・`.log`・`.csv`・`.tsv`・`.patch`・`.diff` はテキストなら対象です。

Markdown 化で選んだファイルに 1 MB（画面の従来の換算に合わせて 1,048,576 バイト）を超えるものがあるときだけ、
書き出し前に名前・サイズ・合計をまとめて1回表示します。「外す」で該当ファイルすべての本文を外し、
「そのまま」で含めます。個別の選択はなく、閉じる／Esc では書き出さず元の選択を保ちます。
右クリックからのコンソールも同じ2択です。対話できないコンソールと、選択を指定しない API 呼び出しは
大きいファイルの本文を除外して進み、確認待ちで停止しません。

出力末尾の「対象外」には、入力一覧にある未変換ファイルの相対パスと理由を残します。
バイナリ／不正な文字コード、除外拡張子、既存のサイズ上限、変換失敗、未対応の文書形式、
選択を外したファイルを区別し、内容は載せません。51件以上は折りたたみ表示にしますが、
名前は全件を同じ Markdown に保存します。対象外だけの入力も「対象外の一覧を書き出す」から保存できます。
ファイルを個別に選んだ場合はその範囲だけ、光学転送時は送るために選んだ範囲だけを記載します。
ディレクトリの探索範囲と、PDF / Office の既存のサイズ上限は変えていません。

## Tailnet からリモコンを開く

`your-ferry-host` では Tailscale Serve の HTTPS `10000` を Ferry の既定ポートへ転送します。

```powershell
tailscale serve --bg --yes --https=10000 http://127.0.0.1:18422
```

リモコンの URL は `https://your-ferry-host.your-tailnet.ts.net:10000/` です。ホスト名と
tailnet 名は、自分の環境の値へ置き換えてください。この経路だけを
元に戻す場合は、ほかの Serve 設定を残したまま次を実行します。

```powershell
tailscale serve --https=10000 off
```

## 実機で未確認のこと

提供元 README にあった「PC 画面と実カメラで成功」という記述は、提供元版の記録です。
今回の修正版では再確認していません。Windows PowerShell 5.1 / WPF / WebView2 での起動、
iPhone / iPad を QR 表示面にする経路、実カメラでの連続受信、複数端末リモコン、
Office 実文書の変換は実機での確認が必要です。

## 開発用ビルド

`.csproj` は開発中のコンパイル確認と単一ファイル生成のために残しています。配布・通常起動には
.NET SDK も、生成済み exe の持ち込みも要りません。

```powershell
dotnet build
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/linux-x64
```

開発用ビルドに必要な SDK は .NET 9 です。

## デスクトップ背景への対応

この版では `install.bat` にデスクトップ背景の登録を追加しています。ファイル・フォルダ選択時は従来の光学転送／Markdown／VBA メニューを維持し、選択した対象を `%1` で渡します。エクスプローラの空白とデスクトップの空白では、対象パスを推測せず既存アプリ画面を直接起動します。

更新後は `install.bat` を再実行してください。`uninstall.bat` の一覧・バックアップ・削除対象にもデスクトップ背景を追加しています。Windows 11 では従来形式の右クリックメニューから利用します。

`powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test-desktop-context.ps1` で4種の登録定義、選択時引数、背景の直接起動、削除定義を確認できます。実際の Explorer 表示・起動は実機で確認してください。
