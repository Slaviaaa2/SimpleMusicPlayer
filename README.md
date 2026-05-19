# SimpleMusicPlayer

最低限のUIで音楽を再生する WPF プレイヤーです。`mp3` だけでなく、Windows が標準デコードできる `mp4` などの音声付きメディアも再生できます。`mkv` / `webm` / `opus` は `ffmpeg.exe` が `PATH` にある場合、`cache` 配下へデコード結果を保存して再生します。`yt-dlp.exe` が `PATH` にある場合は YouTube などの URL から音声を取得し、`cache` に保存して再利用できます。

## Features

- シンプルな単一ウィンドウ
- YouTube などの URL を `yt-dlp` 経由で追加
- シークバー、再生/一時停止、前後移動
- ループ `Off / All / One`
- ファイル複数選択、フォルダをアルバム扱いで読み込み
- フォルダのドラッグ&ドロップ、`Add Album` で既存キュー末尾へアルバム追加
- ドラッグ&ドロップ対応
- URL / デコード済みメディアの永続 `cache`
- 最近再生したアルバム / 曲の履歴を表示し、ダブルクリックで再生し直し
- CLI から初期キューと再生モード指定

## Run

```powershell
dotnet run -- --album "D:\Music\Some Album" --loop all
```

```powershell
dotnet run -- "D:\Music\track1.mp3" "D:\Music\track2.mp4" --index 1 --loop one
```

```powershell
dotnet run -- "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
```

引数なしで起動した場合、カレントディレクトリに対応メディアファイルがあれば、そのフォルダをアルバムとして自動で読み込みます。

## CLI options

- `--album <folder>`: フォルダ内の対応ファイルを名前順で読み込み
- `--shuffle`: 読み込み時にシャッフル
- `--index <n>`: 開始トラック番号。`0` 始まり
- `--loop none|all|one`: 初期ループモード
- `--discord-app-id <id>`: Discord Rich Presence 用の Application ID
- 位置引数: ローカルファイル / フォルダ / `http(s)` URL

## URL playback

- UI の `Add URL` に YouTube などの `http(s)` URL を貼ると、`yt-dlp` で音声を取得してキューへ追加します。
- 取得したメディアは実行ファイル横の `cache\yt-dlp\` に URL ごとのハッシュフォルダで保存され、同じ URL は次回以降そこから即再生します。
- `webm` / `opus` など Windows の標準再生に向かない形式は `cache\transcoded\` に `wav` を残して再利用します。
- `yt-dlp.exe` が `PATH` に無い場合は URL 再生できません。`ffmpeg.exe` があると変換が必要な形式も再生できます。

## Keyboard

- `Space`: 再生 / 一時停止
- `Left` / `Right`: 5 秒シーク
- `Ctrl+Left` / `Ctrl+Right`: 前の曲 / 次の曲

## History

- 右側の `Recent Albums` と `Recent Tracks` に最近の再生履歴を最大 20 件ずつ表示します。
- 履歴は `%LOCALAPPDATA%\SimpleMusicPlayer\history.json` に保存され、次回起動時も残ります。
- アルバム履歴をダブルクリックするとフォルダ全体を再読込し、曲履歴をダブルクリックするとその曲だけを再生します。

## Local build

```powershell
dotnet build -c Release
```

## Release install

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-Release.ps1
```

`Release` の publish 先は既定で `D:\Tools\SimpleMusicPlayer` です。スクリプトは publish 後に Start Menu の `Programs` へ `Simple Music Player` のショートカットを作成し、さらに `yt-dlp.exe` と `ffmpeg.exe` 一式も `tools\` 配下へ自動配置します。

さらに次も自動設定します。

- `D:\Tools\SimpleMusicPlayer` をユーザー `PATH` に追加
- `D:\Tools\SimpleMusicPlayer\tools\yt-dlp\yt-dlp.exe` を自動ダウンロード
- `D:\Tools\SimpleMusicPlayer\tools\ffmpeg\ffmpeg.exe` などを自動ダウンロード
- Explorer のフォルダ右クリックに `Play with Simple Music Player`
- Explorer のフォルダ背景右クリックに `Open here with Simple Music Player`
- 対応メディア拡張子を `Open with` / `Default apps` の候補として登録

これで `cmd` / PowerShell ではアルバムフォルダへ移動してから `SimpleMusicPlayer` だけで起動できます。

既定の再生アプリにしたい場合は、インストール後に Windows の `設定 > アプリ > 既定のアプリ` で `Simple Music Player` を選び、`.mp3` など必要な拡張子へ割り当ててください。Windows 10/11 では既定アプリ自体をアプリ側から強制変更はできないため、候補登録までをこのスクリプトで行います。

ツールの自動ダウンロードを無効にしたい場合は `-SkipToolDownloads` を付けます。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-Release.ps1 -SkipToolDownloads
```

## Discord Rich Presence

Discord のアクティビティに再生中の曲名を出すには、Discord Developer Portal でアプリを作成して `Application ID` を取得し、次のどちらかで渡します。

```powershell
$env:SIMPLE_MUSIC_PLAYER_DISCORD_APP_ID="123456789012345678"
dotnet run -- "D:\Music\track1.mp3"
```

```powershell
dotnet run -- --discord-app-id 123456789012345678 "D:\Music\track1.mp3"
```

Discord デスクトップアプリが起動している環境でのみ反映されます。未設定時は通常どおり再生し、Discord 連携だけ無効になります。

公開済みの `SimpleMusicPlayer.exe` を使う場合は、`exe` と同じフォルダに `.env` を置いても読み込めます。

```dotenv
SIMPLE_MUSIC_PLAYER_DISCORD_APP_ID=123456789012345678
```

## GitHub release

- GitHub Actions は `v0.4.0` のようなタグ push で Windows 向け zip を生成し、そのまま Release に添付します。
- 手動確認だけなら Actions の `workflow_dispatch` から同じビルドを artifact として取得できます。

```powershell
git tag v0.4.0
git push origin main --tags
```
