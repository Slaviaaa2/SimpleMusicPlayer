# SimpleMusicPlayer

ローカル音楽、アルバムフォルダ、動画付き音声ファイル、URL 再生を 1 つのウィンドウで扱えるデスクトッププレイヤーです。`Rust + Slint + libVLC` 製で、軽量・高速に動作します。

> v0.6 で C# (Avalonia + .NET) から Rust へ全面的に書き直しました。UI・機能・データ(再生履歴など)は旧バージョンと互換です。

## できること

- `mp3` `wav` `m4a` `aac` `flac` `ogg` などのローカル再生
- `mp4` `mov` `wmv` `mkv` `webm` などの音声トラック再生
- フォルダをアルバムとしてまとめて読み込み
- URL / ファイルパス / フォルダパスを同じ入力欄から追加
- ドラッグアンドドロップでファイルやフォルダを追加(Windows)
- 最近再生したアルバム / 曲の履歴
- `yt-dlp` による URL 音声取得とキャッシュ
- `ffmpeg` による変換とキャッシュ
- Discord Rich Presence

## 対応 OS

- Windows(メインターゲット。ドラッグ&ドロップ・初回セットアップ・アンインストール対応)
- Linux(バイナリ配布あり。システムの libvlc が必要)
- macOS(ソースビルドのみ。libvlc を pkg-config で解決できる環境が必要 — 公式バイナリ配布は現在準備中)

## 導入ガイド

### 一般ユーザー向け(Windows)

1. GitHub Releases から `SimpleMusicPlayer-vX.Y.Z-win-x64.zip` をダウンロードします。
2. 好きな場所へ展開します。
3. `SimpleMusicPlayer.exe` を起動します。

初回起動時にアプリ内からセットアップ確認が出ます。実行すると次を自動で行います(すべてアプリ本体に内蔵。外部スクリプトはありません)。

- Start Menu ショートカット作成
- `PATH` へのアプリフォルダ追加
- Explorer の右クリックメニュー追加
- `Open with` / `Default apps` 向けの関連付け候補登録
- `yt-dlp` `deno` `ffmpeg` の不足時ダウンロード

アンインストールは Windows の「設定 > アプリ」から行うか、次を実行します:

```powershell
.\SimpleMusicPlayer.exe --uninstall
```

追加スイッチ(旧バージョンと同名):

- `--remove-user-data`: 再生履歴などのローカルデータも削除
- `--remove-cache`: URL 再生キャッシュを削除
- `--remove-bundled-tools`: 同梱ダウンロード済みツールを削除
- `--remove-app-directory`: 展開フォルダ自体も削除

### Linux / macOS

必要な外部ツールは次のどちらかで解決してください。

- `PATH` に `yt-dlp` `ffmpeg` と JS runtime (`deno` `node` `bun` `qjs`) を入れる
- 展開フォルダ配下の `tools/` に置く(例: `tools/yt-dlp/yt-dlp`)

Linux では `libvlc` 本体はシステム側で入れておく前提です。例:

- Debian / Ubuntu: `sudo apt install vlc libvlc-dev`
- Arch: `sudo pacman -S vlc`
- Fedora: `sudo dnf install vlc`

### 何をすれば再生できるか

- ファイルを再生したい: `Open` を押すか、ファイルをウィンドウへドラッグします。
- アルバムごと再生したい: `Add Album` を押すか、フォルダをドラッグします。
- URL を再生したい: 入力欄へ URL を貼って `Add` を押します。
- パスを直接入れたい: 入力欄へファイルパスやフォルダパスを貼って `Add` を押します。

## 使い方

### キーボード操作

- `Space`: 再生 / 一時停止
- `Left` / `Right`: 5 秒シーク
- `Ctrl+Left` / `Ctrl+Right`: 前の曲 / 次の曲

### 履歴

- `Recent Albums` と `Recent Tracks` に最大 20 件ずつ保存します。
- 保存先は各 OS の Local Application Data 配下です(旧 C# バージョンの `history.json` をそのまま引き継ぎます)。
- ダブルクリックでそのまま再生し直せます。

### URL 再生とキャッシュ

- URL 再生には `yt-dlp` が必要です。
- YouTube 系 URL を安定して再生するには JavaScript runtime (`deno` `node` `bun` `qjs`) も必要です。
- `webm` `mkv` `opus` などの変換には `ffmpeg` が必要です。
- 取得済み音声は `cache/yt-dlp/`、変換済み音声は `cache/transcoded/` に保存されます。

## Discord Rich Presence

Discord のアクティビティには、再生中の曲名が自動で表示されます。

## 開発者向け

### 必要なもの

- Rust (stable)
- Windows: Visual Studio Build Tools(vlc-rs が libvlc.dll からインポートライブラリを生成するのに `dumpbin`/`lib` を使用)+ 下記の libvlc ランタイム
- Linux: `libvlc-dev` と `pkg-config`

### Windows での初回セットアップ

libvlc のネイティブランタイムを取得します(`vendor/libvlc/win-x64` に配置され、`.cargo/config.toml` の `VLC_LIB_DIR` が参照します):

```powershell
.\scripts\fetch-libvlc.ps1
```

### ローカル実行

```powershell
cargo run -p smp-app
```

実行には `libvlc.dll` / `libvlccore.dll` / `plugins/` が exe の隣に必要です。デバッグ時は次のいずれかで配置します:

```powershell
Copy-Item vendor\libvlc\win-x64\libvlc.dll,vendor\libvlc\win-x64\libvlccore.dll target\debug\
Copy-Item vendor\libvlc\win-x64\plugins target\debug\ -Recurse
```

CLI オプション(旧バージョン互換):

```powershell
cargo run -p smp-app -- --album "D:\Music\Some Album" --loop all
cargo run -p smp-app -- "D:\Music\track1.mp3" "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
```

- `--album <folder>`: フォルダ内の対応ファイルを名前順で読み込み
- `--shuffle`: 読み込み時にシャッフル
- `--index <n>`: 開始トラック番号。`0` 始まり
- `--loop none|all|one`: 初期ループモード
- 位置引数: ローカルファイル / フォルダ / `http(s)` URL

### テスト

```powershell
cargo test --workspace
```

### 配布用パッケージ(Windows)

```powershell
.\scripts\package-release.ps1
```

`publish\SimpleMusicPlayer-win-x64\` に exe + libvlc ランタイム一式が出力されます。

### ワークスペース構成

```
crates/
  smp-core/       ドメインモデル・キュー・履歴永続化(純粋ロジック)
  smp-playback/   libvlc ラッパー、yt-dlp / ffmpeg キャッシュ、外部プロセス実行
  smp-discord/    Discord Rich Presence
  smp-setup/      Windows: ショートカット・PATH・レジストリ・ツールDL・アンインストール
  smp-app/        Slint UI + 状態管理(バイナリ本体)
```

## GitHub Release

- GitHub Actions は `v0.6.0` のようなタグ push で `win-x64` `linux-x64` をビルドし、それぞれ zip を Release に添付します。
- `workflow_dispatch` でも同じ artifact を取得できます。

```powershell
git tag v0.6.0
git push origin main v0.6.0
```
