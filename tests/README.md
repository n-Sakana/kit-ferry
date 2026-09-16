# 回帰テスト

## 2026-09-16 対象外一覧と大きいファイルの確認

C# は Linux / Windows とも22件成功、両OSのビルドは警告0・エラー0でした。
`optical-core.test.js` は20件、`browser_regression.py` は既存16項目に3項目を追加して19項目成功です。
固定除外、相対パスと理由、121件の省略なし出力、対象外だけの入力、1 MB の境界と一括除外／包含を固定しました。
ブラウザでは小さい JSONL の確認なし変換、大きいファイルの一括2択、Esc による中止と選択保持、
対象外だけの書き出しを操作しています。この試験の API は従来どおりモックです。

修正前 `89b6566` へ固定除外試験だけを移すと `excluded suffix admitted: icon.svg` で失敗しました。
同じ変更前の版には、隠しファイル試験の入力が LF 固定で期待値が OS 標準改行という不一致があり、
Windows では `explicit hidden text content lost` を再現しました。期待値・skip・閾値を変えず、
その試験入力だけを OS 標準改行へ合わせ、別の試験で LF / CRLF の本文保持を確認しています。

Windows ノートでは Explorer の `ferry.vbs` をダブルクリックし、OS のフォルダ選択から実サーバーへ接続しました。
ソース・CP932・BOM・拡張子なし・JSONL・既存文書等の16件を本文へ、意図的な破損 DOCX と
512 MiB 超の PDF を対象外へ書き出し、隣のボタンで出力フォルダを開いて1枚の Markdown を照合しました。
別の入力では大きい2ファイルを「外す」で311バイト、「そのまま」で2,097,324バイトの出力になり、
双方とも失敗0件。Esc で選択が残り、対象外のみのフォルダ／単一 SVG も名前だけを書き出せました。
画面のクリックはヘッドレス、起動と OS ダイアログだけ実画面です。検証用 Ferry と開いた窓は閉じました。
Windows で Hidden 属性のない `.gitignore` もフォルダ入力では候補に出さず、本文を取り込まず名前だけ残ることを確認しました。
16件をまとめた実出力は44,758バイト、SHA-256 は `e993da2d23c201124b8baa98e698ba550907279eaed235c12d660616b119d82a` です。
先生の検収、実カメラ・モバイル・VBA の再検証、Linux / macOS の GUI は今回の確認範囲外です。

## 2026-09-16 ソースファイル対応の確認

`dotnet run --project tests/Ferry.Regression.csproj -c Release` は Linux / Windows でそれぞれ15件成功しました。
既存11件に、ソースと拡張子なしのテキスト、BOM / CP932、不正符号列とバイナリの除外、
全文再検査とコードフェンスの4群を追加しています。修正前は `Program.cs` の対象判定で失敗することも確認しました。
`dotnet build Ferry.csproj -c Release` は両OSで警告0・エラー0、`optical-core.test.js` は20件成功、
`browser_regression.py` は16項目成功です。ブラウザ試験の API モックと合成画像の範囲は従来どおりです。

別途、Windows PowerShell 5.1 の通常起動と OS のフォルダ選択を使い、ソース・CP932・UTF-8 BOM・
拡張子なし・既存の PDF / Office 等の12件を1枚に書き出し、出力フォルダと Markdown を開くまで確認しました。
PNG・EXE の一覧除外と、完了後の選択リセットも確認しています。実カメラとモバイルは今回の確認範囲に含みません。
ファイル単体の選択でも、OS ダイアログに `.cs` と `Makefile` が表示され、それぞれ1件を失敗0で書き出せました。

以下の「今回」は、元の 2026-09-08 提供時点の検証記録です。

## 実行済みと未実行の区別

今回実行できたのは JavaScript 単体試験、および Chromium + 同梱 WASM による合成画像・仮想カメラ試験です。
**C# バックエンドのビルド／実行、実カメラ、Windows 起動、実 Tailnet、Office 変換の試験は実行していません。**
`RegressionTests.cs` は別環境で確認できるよう追加したテストで、今回の「合格件数」には含めません。

## JavaScript 単体試験（実行済み: 20 / 20）

プロジェクト直下で実行します。外部 npm パッケージは不要です。使用した Node.js は 22.16.0 です。

```sh
node --test tests/optical-core.test.js
node --check web/app.js
node --check web/optical-core.js
node --check web/qr-worker.js
node --check web/zxing-reader.js
node --check web/service-worker.js
```

フレームのヘッダー検証、受信重複排除、異なる転送の排除、受信キュー上限、順序制御、通信再試行、
停止後の遅延応答、ビット圧縮の展開、座標追跡と再探索を確認します。
実行ログは `results/node-tests.txt` にあります。

## Chromium / WASM 試験（実行済み: 機能チェック 16 / 16、修正設定の画像 15 / 15）

テスト専用の Python パッケージをインストールします。Ferry 本体の実行には不要です。
使用した Python は 3.13.5、Chromium は 144.0.7559.96 です。

```sh
python -m pip install -r tests/requirements.txt
python -m playwright install chromium
python tests/browser_regression.py
```

システムに Chromium がある場合はそれを利用します。明示指定は環境変数
`PLAYWRIGHT_CHROMIUM_EXECUTABLE` で行います。未指定なら Playwright 同梱ブラウザも使用できます。
通常の試験は一時的な localhost HTTP サーバーを使用します。

今回の環境ではブラウザの URL ナビゲーションが制限されていたため、ポリシーを変更せず、
次の **インメモリーモード** で実行しました。

```sh
python tests/browser_regression.py --in-memory
```

このモードでは HTML / CSS / JS / WASM / 合成 PNG をメモリーから渡します。
Worker のリソース読み込み部分のみテスト側で置き換え、製品のアプリ処理と読み取り処理を動かします。
WASM は同梱バイナリそのものです。HTTP API はどちらのモードでもモックであり、
C# サーバーが動いた証拠ではありません。仮想カメラは PNG 12 枚を Canvas に順次描き、
`captureStream(30)` → video → Worker → 実 WASM → モック受信 API の経路を通します。
モックは最初の受信要求を HTTP 503 相当で失敗させ、異なる 6 フレームの受信後に完了を返します。
保存済みファイルの検算や実転送時間は、このブラウザ試験では検証していません。

`fixtures/qr-worker-original.js` は提供 ZIP の元 Worker をそのまま残した比較用ファイルです。
`generated/` の合成画像は毎回生成され、配布 ZIP には含めません。
結果は `results/browser-results.json`、ログは `results/browser-tests.txt`、画面例は `results/receive-ui.png` です。
画面例の速度・保存完了表示はモック値であり、実機での保存成功を示しません。

画像比較は「旧 2953-byte QR + 旧 Worker」と「新 1000-byte QR + 新 Worker」を同じ画面上の大きさで比較します。
旧設定 13 / 15、新設定 15 / 15 でした。QR 密度と読み取り処理の両方を変えているため、
アルゴリズム単独の改善率ではありません。実カメラでの成功率でもありません。
ROI 測定のみは同一の 1000-byte QR 画像を用い、旧全画面処理と新追跡領域処理を比較します。

## C# バックエンド試験（追加済み・未実行）

.NET 9 SDK を用いて実行します。NuGet のテストランナーは不要です。
SDK と C# コンパイラーを今回の環境で用意できなかったため、このプロジェクト自体のビルドも未確認です。

```sh
dotnet build Ferry.csproj -c Release
dotnet run --project tests/Ferry.Regression.csproj -c Release
```

11 件を収録しています。CLI 引数、欠落・順不同・重複のある噴水符号、packed QR と既存 RGBA の画素一致、
複数表示トークン、誤り訂正容量、小さいファイル、受信から保存・検算・停止までのライフサイクル、
破損データ、ファイル選択、並行出力ディレクトリー、プレーンテキストの Markdown 化が対象です。
`LangVersion=5` を指定していますが、.NET 9 上のコンパイル成功だけでは Windows PowerShell 5.1 /
.NET Framework / WPF / WebView2 の互換性までは保証しません。

## Windows と実カメラの確認

旧版を終了し、ZIP を別フォルダーに全展開して `ferry.vbs` を起動してください。
起動に失敗した場合は `ferry.cmd` の画面と `%LOCALAPPDATA%\Ferry\logs` が確認箇所です。
PC とスマホの双方を新しいサーバーから再読み込みし、小さなテキストを送受信した後、
ファイル選択・Markdown・VBA・複数リモコン・通信を数秒切る試験へ進めます。
QR 位置、距離、照明、撮影解像度と fps、実際の転送時間、保存ファイルの SHA-256 を記録すると比較できます。
