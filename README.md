# SimpleMusicPlayer

ローカル音楽、アルバムフォルダ、動画付き音声ファイル、URL 再生を 1 つのウィンドウで扱えるクロスプラットフォームデスクトッププレイヤーです。`Avalonia + LibVLC` ベースなので、Windows / macOS / Linux で同じコードを動かせます。

## できること

- `mp3` `wav` `m4a` `aac` `flac` `ogg` などのローカル再生
- `mp4` `mov` `wmv` `mkv` `webm` などの音声トラック再生
- フォルダをアルバムとしてまとめて読み込み
- URL / ファイルパス / フォルダパスを同じ入力欄から追加
- ドラッグアンドドロップでファイルやフォルダを追加
- 最近再生したアルバム / 曲の履歴
- `yt-dlp` による URL 音声取得とキャッシュ
- `ffmpeg` による変換とキャッシュ
- Discord Rich Presence

## 対応 OS

- Windows
- macOS
- Linux / Unix 系デスクトップ

## 導入ガイド

### 一般ユーザー向け

1. GitHub Releases から自分の OS / CPU に合う配布アーカイブをダウンロードします。
2. 好きな場所へ展開します。
3. `SimpleMusicPlayer` を起動します。

Windows 版だけは、初回起動時にアプリ内からセットアップ確認が出ます。これを実行すると次を自動で行います。

- Start Menu ショートカット作成
- `PATH` へのアプリフォルダ追加
- Explorer の右クリックメニュー追加
- `Open with` / `Default apps` 向けの関連付け候補登録
- `yt-dlp` `deno` `ffmpeg` の不足時ダウンロード

Windows で手動実行したい場合:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-SimpleMusicPlayer.ps1
```

macOS / Linux では Windows 用セットアップスクリプトは使いません。必要な外部ツールは次のどちらかで解決してください。

- `PATH` に `yt-dlp` `ffmpeg` と JS runtime (`deno` `node` `bun` `qjs`) を入れる
- 展開フォルダ配下の `tools/` に置く

配置例:

- `tools/yt-dlp/yt-dlp`
- `tools/ffmpeg/ffmpeg`
- `tools/deno/deno`

Windows ではそれぞれ `.exe` 付きでも同様に認識します。

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
- 保存先は各 OS の Local Application Data 配下です。
- ダブルクリックでそのまま再生し直せます。

### URL 再生とキャッシュ

- URL 再生には `yt-dlp` が必要です。
- YouTube 系 URL を安定して再生するには JavaScript runtime (`deno` `node` `bun` `qjs`) も必要です。
- `webm` `mkv` `opus` などの変換には `ffmpeg` が必要です。
- 取得済み音声は `cache/yt-dlp/`、変換済み音声は `cache/transcoded/` に保存されます。

## Discord Rich Presence

Discord のアクティビティには、再生中の曲名が自動で表示されます。

## 開発者向け

### ローカル実行

UI の起動自体は次でできます。

```powershell
dotnet run
```

実際の再生確認は、対象 OS の RID を付けてネイティブ `LibVLC` を一緒に解決する前提を推奨します。Linux は RID publish に加えてシステム `libvlc` を入れてください。

```powershell
dotnet run -r win-x64
```

例:

```powershell
dotnet run -- --album "D:\Music\Some Album" --loop all
```

```powershell
dotnet run -- "D:\Music\track1.mp3" "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
```

### CLI オプション

- `--album <folder>`: フォルダ内の対応ファイルを名前順で読み込み
- `--shuffle`: 読み込み時にシャッフル
- `--index <n>`: 開始トラック番号。`0` 始まり
- `--loop none|all|one`: 初期ループモード
- 位置引数: ローカルファイル / フォルダ / `http(s)` URL

### ビルド

```powershell
dotnet build -c Release
```

### 配布用 publish

OS ごとに `RuntimeIdentifier` を指定して publish します。

Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1 -RuntimeIdentifier win-x64
```

macOS Apple Silicon:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1 -RuntimeIdentifier osx-arm64
```

Linux x64:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1 -RuntimeIdentifier linux-x64
```

既定の出力先は `.\publish\SimpleMusicPlayer-<rid>\` です。Windows publish だけ `Install-SimpleMusicPlayer.ps1` を同梱します。

## GitHub Release

- GitHub Actions は `v0.5.2` のようなタグ push で `win-x64` `osx-x64` `osx-arm64` `linux-x64` を publish し、それぞれ zip を Release に添付します。
- `workflow_dispatch` でも同じ artifact を取得できます。

```powershell
git tag v0.5.2
git push origin main v0.5.2
```
