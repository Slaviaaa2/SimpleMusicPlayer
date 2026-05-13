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

## CLI options

- `--album <folder>`: フォルダ内の対応ファイルを名前順で読み込み
- `--shuffle`: 読み込み時にシャッフル
- `--index <n>`: 開始トラック番号。`0` 始まり
- `--loop none|all|one`: 初期ループモード

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

## GitHub release

- GitHub Actions は `v0.1.0` のようなタグ push で Windows 向け zip を生成し、そのまま Release に添付します。
- 手動確認だけなら Actions の `workflow_dispatch` から同じビルドを artifact として取得できます。

```powershell
git tag v0.1.0
git push origin main --tags
```
