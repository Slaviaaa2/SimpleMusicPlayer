# SimpleMusicPlayer

最低限のUIで音楽を再生する WPF プレイヤーです。`mp3` だけでなく、Windows が標準デコードできる `mp4` などの音声付きメディアも再生できます。

## Features

- シンプルな単一ウィンドウ
- シークバー、再生/停止、前後移動
- ループ `Off / All / One`
- ファイル複数選択、フォルダをアルバム扱いで読み込み
- フォルダのドラッグ&ドロップ、`Add Album` で既存キュー末尾へアルバム追加
- ドラッグ&ドロップ対応
- CLI から初期キューと再生モード指定

## Run

```powershell
dotnet run -- --album "D:\Music\Some Album" --loop all
```

```powershell
dotnet run -- "D:\Music\track1.mp3" "D:\Music\track2.mp4" --index 1 --loop one
```

引数なしで起動した場合、カレントディレクトリに対応メディアファイルがあれば、そのフォルダをアルバムとして自動で読み込みます。

## CLI options

- `--album <folder>`: フォルダ内の対応ファイルを名前順で読み込み
- `--shuffle`: 読み込み時にシャッフル
- `--index <n>`: 開始トラック番号。`0` 始まり
- `--loop none|all|one`: 初期ループモード
- `--discord-app-id <id>`: Discord Rich Presence 用の Application ID

## Keyboard

- `Space`: 再生 / 一時停止
- `Left` / `Right`: 5 秒シーク
- `Ctrl+Left` / `Ctrl+Right`: 前の曲 / 次の曲

## Local build

```powershell
dotnet build -c Release
```

## Release install

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-Release.ps1
```

`Release` の publish 先は既定で `D:\Tools\SimpleMusicPlayer` です。スクリプトは publish 後に Start Menu の `Programs` へ `Simple Music Player` のショートカットも作成します。

さらに次も自動設定します。

- `D:\Tools\SimpleMusicPlayer` をユーザー `PATH` に追加
- Explorer のフォルダ右クリックに `Play with Simple Music Player`
- Explorer のフォルダ背景右クリックに `Open here with Simple Music Player`

これで `cmd` / PowerShell ではアルバムフォルダへ移動してから `SimpleMusicPlayer` だけで起動できます。

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

- GitHub Actions は `v0.2.0` のようなタグ push で Windows 向け zip を生成し、そのまま Release に添付します。
- 手動確認だけなら Actions の `workflow_dispatch` から同じビルドを artifact として取得できます。

```powershell
git tag v0.2.0
git push origin main --tags
```
