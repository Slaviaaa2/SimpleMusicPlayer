# SimpleMusicPlayer: C# → Rust 全面書き直し計画

## Context

現行の `SimpleMusicPlayer` は Avalonia + .NET(net10.0) + LibVLCSharp で書かれたクロスプラットフォーム音楽/動画プレイヤーで、ユーザーは動作の遅さ(.NETランタイムのオーバーヘッド)を理由に Rust への全面書き直しを希望している。UI の見た目・操作感はできるだけ変えない。

技術選定はユーザーと相談の上、以下で確定した:
- GUI: **Slint**(宣言的マークアップ、ネイティブGPU描画、WebView不使用)
- 再生バックエンド: **libvlc を継続**(`vlc-rs` バインディング経由でFFI、現行と同じネイティブライブラリ)
- スコープ: コア機能優先、ただし Windows インストーラ/アンインストーラ(現行 PowerShell スクリプト群)は「もはや作り直しレベルでいい」というユーザー方針により、PowerShell 依存をやめて Rust バイナリ自身に統合する形で再設計する

## アーキテクチャ

Cargo workspace(リポジトリ直下に新設、C# プロジェクトは Rust 側が機能パリティに達するまで併存させ、最後にまとめて削除):

```
Cargo.toml (workspace)
crates/
  smp-core/       ドメインモデル・純粋ロジック・履歴/設定の永続化
  smp-playback/   vlc-rs ラッパー、ffmpeg/yt-dlp キャッシュ、外部プロセス実行
  smp-discord/    Discord Rich Presence
  smp-setup/      Windows専用: レジストリ/ショートカット/PATH/ツール自動DL(旧ps1群の置き換え)
  smp-app/        バイナリ本体: Slint UI + 状態管理オーケストレーション
assets/           既存 Assets/app-icon.ico・png を流用
```

詳細な各クレートの設計(現行C#ファイルとの対応関係)は `C:\Users\zeros\.claude\plans\glimmering-rolling-sketch.md` に記載(承認済みプラン原本)。要点:

- `smp-core`: `PlaybackItem`/`PlaybackSourceKind`/`LoopMode`/`PlaybackQueueNavigator`/`MediaFileTypes`/`PlaybackSourceCollector`/`CliOptions`/履歴永続化(`JsonFileStore<T>`/`PlaybackHistoryStore`/`PlaybackHistoryService`)/`AppSetupState`/`ProcessOutputFormatter`
- `smp-playback`: `ExternalProcessRunner`/`ToolPathResolver`/`JavaScriptRuntimeResolver`/`FfmpegAudioCache`/`YtDlpAudioCache`(664行の最重要移植対象)/`PlaybackController`(vlc-rs)
- `smp-discord`: `discord-rich-presence` クレートで `DiscordPresenceService.cs` を再現
- `smp-setup`(Windows専用): PowerShell群(`Install-Release.ps1`等)を廃止し `winreg`/`windows`クレートでレジストリ・ショートカット・PATH・ツール自動DLをバイナリに統合
- `smp-app`: Slint UI(`ui/*.slint`)+ `app_state.rs`(現行 `MainWindowViewModel.cs`+`MainWindow.axaml.cs` 相当)。Windowsネイティブドラッグ&ドロップを `windows`クレートのOLE `IDropTarget` で自前実装。

## フェーズ分割

- **Phase 0 — スパイク検証** [完了]
- **Phase 1 — smp-core 移植** [完了]
- **Phase 2 — smp-playback 移植** [完了]
- **Phase 3 — Slint UIシェル構築** [完了]
- **Phase 4 — UIと状態管理の結線** [完了(一部簡略化あり、下記参照)]
- **Phase 2 — smp-playback 移植**
- **Phase 3 — Slint UIシェル**
- **Phase 4 — UIと状態管理の結線**
- **Phase 5 — Discord Presence / Windows D&D / smp-setup**
- **Phase 6 — パッケージング・CI**

各フェーズ末に一度立ち止まり、次フェーズに進む前に問題がないか報告する。

---

## Phase 0 スパイク検証結果 [完了]

事前のクレート調査(crates.io / lib.rs 経由)では `vlc-rs` の crates.io 公開版が2018年で止まっており、`vswhom-sys`(Windows向けVisual Studio検出の依存クレート)にリスクの疑いがあった。**このマシンの実際のツールチェーン(Rust 1.96.1、VS Build Tools at `D:\Tools\BuildTools`、現行C#プロジェクトが `obj/verify-publish/libvlc/win-x64` に既にベンダリング済みの `libvlc.lib`/`libvlc.dll` 一式)を使って実機ビルド・実行検証を行った**(`%TEMP%\claude\...\scratchpad\vlc-rs-spike\` に使い捨てプロジェクトを作成)。

### 結果: go(ただし1点ワークアラウンド必須)

1. **`vlc-rs` を公式VideoLAN GitLabミラー(`https://code.videolan.org/videolan/vlc-rs.git`)からgit依存として取得しビルド** → 素の状態では `vswhom-sys`(VS検出用の依存)がレジストリAPI(`RegOpenKeyExA`等)をリンクできず `LNK2019/LNK1120` で失敗。
2. **ワークアラウンド確認**: `RUSTFLAGS="-C link-arg=advapi32.lib"` を設定することでビルド成功を確認。→ 本実装では `smp-playback/.cargo/config.toml` にこのリンカ引数を追加する(あるいはワークスペースルートの `.cargo/config.toml` に `[target.x86_64-pc-windows-msvc] rustflags = ["-C", "link-arg=advapi32.lib"]`)。
3. **ビルド後、実際に `libvlc.dll`/`libvlccore.dll`/`plugins/` をバイナリ横にコピーして実行** → `vlc::version()` が `"3.0.21 Vetinari"` を正常に返すことを確認。**libvlc初期化・FFI呼び出しの実導線を実証済み。**
4. **APIサーフェス確認**(ソース直接確認、`code.videolan.org/videolan/vlc-rs` master @ `12a22eb8`, 2022-05-09時点): `Instance::new`、`Media::new_path`/`new_location`/`duration()`/`event_manager()`、`MediaPlayer::play`/`set_pause`/`pause`/`stop`/`get_time`/`set_time`/`get_position`/`set_position`/`state()`/`event_manager()`、`EventManager::attach(EventType, callback)` と `Event::MediaPlayerPlaying`/`MediaPlayerEndReached`/`MediaPlayerEncounteredError` が揃っており、現行 `PlaybackController.cs` の状態遷移をそのまま再現できる。

Windows以外(Linux/macOS)は `pkg-config` 経由でシステムlibvlcにリンクする分岐(`link_vlc_with_pkgconfig`)のため、vswhom関連の問題は発生しない見込み(未検証、Phase 2実装時に確認)。

### Slint検証

同様に使い捨てプロジェクトで実際に `cargo build` して以下を確認(Slint 1.17.1):

- `FocusScope` + `key-pressed` コールバック + `forward-focus` によるウィンドウレベルのキー捕捉 → コンパイル成功、現行の Space/Left/Right/Ctrl+Left/Ctrl+Right 捕捉に使える。
- `TouchArea` + `PointerEventKind.down`/`up` によるドラッグ開始/終了の明示的検知 → コンパイル成功、現行の `_isDraggingSeekBar` 相当のシークバー自作に使える。
- `slint::Window::window_handle()`(`raw-window-handle-06` feature)で raw window handle(Windowsでは HWND)を取得可能 → docs.rs で確認済み。Windowsネイティブドラッグ&ドロップ(OLE `IDropTarget`)実装に使う。
- `slint::spawn_local` は Slintイベントループ内の Future 用。Tokio の Future をそのまま渡すと駆動されない旨が明記されている → **設計方針: tokio タスクは別スレッドで動かすバックグラウンドの tokio runtime 上で実行し、結果は `slint::invoke_from_event_loop` でUIスレッドに戻す**(現行の `Dispatcher.UIThread.Post` と同型)。`spawn_local` 自体は使わない。

### 結論
Phase 0 の2大リスクはいずれも実機検証済みで、致命的な障害なし。Phase 1(smp-core移植)へ進行する。

---

## Phase 1 実施結果 [完了]

リポジトリ直下に Cargo workspace を新設(`Cargo.toml` + `crates/smp-core/`)。`smp-core` に以下を移植し、36件のユニットテストを追加(全てパス、`cargo clippy --workspace --all-targets` も警告ゼロ):

- `playback_item.rs` — `PlaybackItem`/`PlaybackSourceKind`(未使用だった2引数レガシーコンストラクタは移植対象から除外。`grep` で `new PlaybackItem(` の呼び出しが無いことを確認済み)
- `loop_mode.rs` — `LoopMode`
- `queue_navigator.rs` — `get_previous_index`/`get_next_index`
- `media_file_types.rs` — 対応拡張子/要トランスコード拡張子
- `source_collector.rs` — `collect()`(URL判定はクロージャ注入、C#の`Func<string,bool> isSupportedUrl`と同型)
- `cli_options.rs` — `CliOptions::parse`(`TryReadValue`のインデックス制御込みで挙動を1:1再現)
- `process_output.rs` — `build_failure_details`
- `history/`(`entries.rs`/`json_store.rs`/`store.rs`/`service.rs`) — `JsonFileStore<T>`/`PlaybackHistoryStore`/`PlaybackHistoryService`。**JSON項目名は`#[serde(rename_all = "PascalCase")]`で旧C#版の`history.json`とキー互換にし、既存ユーザーの履歴をそのまま読み込めるようにした**(plan原本にない追加判断)。`PlaybackHistoryService`はC#の`ObservableCollection`直接バインディングに代えて自前の`Vec`を保持する設計に変更(UIフレームワーク非依存を保つため)。
- `setup_state.rs` — `AppSetupState`/`AppSetupStateStore`(同様にPascalCaseキー互換)

### 実装上の判断・注意点(次フェーズ以降のための記録)
- `PlaybackItem::from_url`/`from_playlist_track`は、表示名未指定時に**URLホスト名ではなく生URL文字列**をdisplay_nameにする。これはC#の`FromUrl(url, displayName = null) => new(url, ..., displayName ?? url)`が`BuildFallbackName`のホスト抽出分岐を実質デッドコード化している挙動を忠実に再現した結果(最初テストを書いた際に誤った期待値でテストが落ち、C#側を再確認して判明)。
- `rand`は0.8ではなく現行の**0.10系**(`rand::rng()`/`SliceRandom::shuffle`)を使用。`chrono`は`DateTime<Local>`が`serde`機能で直接(de)シリアライズ可能なことをdocs.rs実地確認済み。
- `AppSetupStateStore::normalize_path`は`.NET`の`Path.GetFullPath`(`..`/`.`の字句正規化込み)を完全再現していない(絶対パス化+末尾セパレータ除去のみ)。実際の呼び出し元は常にexeの自身のディレクトリ(既にクリーンな絶対パス)なので実用上問題ないが、Phase 5でsmp-setupから使う際にこの前提が崩れないか要再確認。
- `.gitignore`に`target/`を追加。バイナリを生成するworkspaceのため`Cargo.lock`はコミット対象のまま(あえて無視しない)。

次: Phase 2(smp-playback: ExternalProcessRunner/ToolPathResolver/JavaScriptRuntimeResolver/FfmpegAudioCache/YtDlpAudioCache/PlaybackController)。

---

## Phase 2 実施結果 [完了]

`crates/smp-playback` を新設し、以下を移植(ユニットテスト14件追加、workspace全体で計50件パス、`cargo clippy --workspace --all-targets` 警告ゼロ):

- `external_process.rs` — `tokio::process::Command` + `tokio_util::sync::CancellationToken` による `ExternalProcessRunner`。Windows専用の `taskkill /T /F` によるプロセスツリーkill込み(yt-dlpがffmpegを子プロセスとして起動するケースの孤児化防止)。実プロセス(`cmd.exe`)を起動・キャンセルするテストを追加し、これも実機で成功。
- `tool_path_resolver.rs` / `js_runtime_resolver.rs` — `tools/<name>/`→`tools/`→PATHの探索順を1:1移植。
- `ffmpeg_cache.rs` / `ytdlp_cache.rs` — sha256ベースのキャッシュキー、キーごとの非同期ロック(`tokio::sync::Mutex`)、yt-dlpのメタデータ/プレイリストJSON解析、JS runtime不足時の`-U`更新リトライ、失敗メッセージ構築まで含めて逐語的に移植。
- `playback_controller.rs` — `vlc-rs`(gitクレート)ラッパー。C#のイベント(`PlaybackStarted`/`PlaybackEnded`/`PlaybackFailed`)は`tokio::sync::mpsc`チャンネル経由の`PlaybackEvent`列挙型に置き換え(libvlcの内部コールバックスレッドからUIスレッドへの橋渡しは、受信側であるPhase 4のapp_stateが`slint::invoke_from_event_loop`で行う設計)。

### 実機検証(GUI無しCLIハーネス)
`crates/smp-playback/examples/play_file.rs` を作成し、実際に音声を再生して確認:
- ローカルwav再生: `C:\Windows\Media\Alarm01.wav` → トランスコード不要と判定 → 再生開始 → `duration=5.572s` を正しく取得 → 自然終了で`Ended`イベント受信。
- トランスコード経路: ffmpegで生成した3秒のopusファイルを再生 → `requires_transcode=true`を検出 → 実際にffmpegサブプロセスを起動してwavへ変換 → `target/debug/examples/cache/transcoded/<hash>.wav` が生成される → 変換後ファイルを再生して`duration=3s`を正しく取得 → `Ended`。

yt-dlp経由のURL取得は、このマシンに`yt-dlp`バイナリが無いため実機確認はできていない(ロジック自体はJSON解析・URL判定・失敗メッセージ組み立てを含めユニットテストで担保)。実際のURL再生確認はPhase 4(UI結線)以降、`yt-dlp`を導入した上で改めて行う。

### 実装上の判断・注意点
- `sha2 0.11`の`Digest::digest()`戻り値(`Array<u8,...>`)は事前情報と異なり`LowerHex`を実装していなかった(ビルドエラーで発覚)。`hex`クレート(`hex::encode`)で代替。
- `vlc-rs`のWindowsビルドスクリプトが`vswhom`のVS検出処理で一度`STATUS_ACCESS_VIOLATION`を起こしたが、再実行で解消(既知の不安定さとして記録。CI導入時はリトライ前提にするか、より安定した代替手段を検討する余地あり)。
- `vlc-rs`の`MediaPlayer`に`get_length`は存在しない(LibVLCSharpの`MediaPlayer.Length`と非対称)。`Media::duration()`(`media_player.get_media()`経由)で代替し、C#と同じ「未確定時は0扱い」のガードを実装。
- `MediaPlayer::play()`は引数を取らず、`set_media`→`play()`の2段階呼び出しになる(LibVLCSharpの`Play(media)`単発呼び出しと非対称)。

次: Phase 3(Slint UIシェル構築)。

---

## Phase 3 実施結果 [完了]

`crates/smp-app`(バイナリ名 `SimpleMusicPlayer`)を新設し、`ui/app-window.slint` に `MainWindow.axaml` のレイアウト・配色を移植した(まだ再生ロジックは未結線、`main.rs`はダミーデータを流し込んで見た目だけ確認できる状態)。

- 配色はXAMLの値をそのまま再現: 背景 `#121212`、文字 `#F3F3F3`、プライマリボタン `#3A7AFE`、テキスト入力 `#181818`、リスト背景 `#191919`、削除ボタン `#2A2020`/`#553232`/`#FFD7D7` など。
- std-widgetsの`Button`/`LineEdit`は個別インスタンスへの色指定が効かないため、`Rectangle`+`TouchArea`+`TextInput`から`AppButton`/`AppTextInput`/`DeleteButton`/`Badge`/`SeekSlider`を自作(むしろ現行XAMLも独自スタイル定義なので相性が良い)。
- 削除ボタンのゴミ箱アイコンはXAMLの`Path`データ(`M5,2 L11,2 ...`)をSlintの`Path.commands`にほぼそのまま移植できた(SVGパス文字列をそのまま受け付ける)。
- キーボードショートカット(Space/Left/Right/Ctrl+Left/Right)はルート`FocusScope`の`key-pressed`(bubbling、`capture-key-pressed`ではない)で捕捉する設計とした。`TextInput`にフォーカスがある間はテキスト編集が先に処理され、上のFocusScopeまでバブルしてこないため、C#版の「入力欄フォーカス中はSpaceキーが再生/一時停止に化けない」挙動と一致する。
- WrapPanel相当の自動折り返しはSlintに無いため、トランスコートボタン列は`HorizontalLayout`で折り返し無しに簡略化(MinWidth=560pxの範囲では実用上問題ない想定。既知の差分としてここに記録)。

### 実機ビルド時に判明した差異(すべて`cargo build`のコンパイルエラーで検出・修正)
- `import { TabWidget, Tab } from "std-widgets.slint";` は誤り。`Tab`は`TabWidget`をインポートすれば暗黙に使える特殊子要素で、個別importすると `No exported type called 'Tab'` エラーになる。
- `ListView`はstd-widgetsから明示的にimportする必要がある(グローバルではない)。
- Slintでは同一要素に`width`/`height`(固定値)と`min-width`/`min-height`を同時指定できない(`Cannot specify both 'height' and 'min-height'`)。ウィンドウの初期サイズ+リサイズ下限を両立するには`preferred-width`/`preferred-height` + `min-width`/`min-height`を使う。

### 確認方法についての注記
UIの視覚確認のためWin32 API経由でウィンドウをスクリーンショットしようとしたところ、ウィンドウハンドルの取得に失敗し、ユーザーの実際のデスクトップ画面(無関係な個人的内容)を誤ってキャプチャする事故が発生した。該当ファイルは内容を確認せず即削除し、プロセスも終了した。**以後、この手法によるスクリーンショット確認は行わない。** `cargo build`が通ることと`.slint`ソースの目視レビューで構造の正しさを確認し、実際の見た目の確認はユーザー自身が `target/debug/SimpleMusicPlayer.exe` を起動して行う運用とする。

次: Phase 4(UIと状態管理の結線)。

---

## Phase 4 実施結果 [完了・一部簡略化あり]

`crates/smp-app/src/app_state.rs`(`MainWindowViewModel.cs`+`MainWindow.axaml.cs`相当)、`resolve.rs`(`ResolvePlaybackPathAsync`相当)、`ui_bridge.rs`(データ変換)を実装し、Slint UIに全コマンドを結線した。

### スレッドモデル
- `AppState`はスレッドローカル(`thread_local! STATE: RefCell<Option<AppState>>`)で保持。理由: `vlc-rs`の`MediaPlayer`等は`!Send`な生ポインタを持ち、`Rc<RefCell<AppState>>`を`slint::invoke_from_event_loop`(`Send`クロージャが必要)へ直接キャプチャできないため。バックグラウンドの`tokio::runtime`(マルチスレッド)で非同期処理(yt-dlp/ffmpeg呼び出し)を実行し、結果は`Send`なデータのみ(`String`/`enum`)を`invoke_from_event_loop`経由でUIスレッドに戻し、UIスレッド上で`STATE.with(...)`により本体状態を更新する設計。
- 再生位置の定期更新は`slint::Timer`(250ms、`Repeated`)で`_positionTimer`を再現。
- libvlcのイベント(Playing/EndReached/EncounteredError)は`PlaybackController`が`tokio::sync::mpsc`で送出し、専用の常駐タスクが受信して`invoke_from_event_loop`でUIスレッドに反映。

### 実装した機能
Open(ファイル選択)/Add Album(フォルダ選択、`rfd`使用)/Reverse/Prev/Play-Pause/Next/Loop切替/URL・パス入力欄からの追加/YouTubeプレイリスト検出/Recent Albums・Recent Tracksの記録・削除・ダブルクリック再生/キューのダブルクリック再生/シークバードラッグ/キーボードショートカット(Space, Left/Right, Ctrl+Left/Right)/トランスコード・yt-dlpダウンロードの進捗ステータス表示。

### 意図的な挙動改善(C#の実装漏れと判断した箇所)
`MainWindow.axaml.cs`の`StartTrackAsync`は `_isPreparingTrack = true` を設定した直後に `StopPlayback(clearSource: true)` を呼んでおり、`StopPlayback`内部で無条件に `_isPreparingTrack = false` へ戻してしまうため、**トラック読み込み中でも進捗バー(`IsPreparationVisible`)が実質的に一度も表示されない**という現行C#版の潜在バグを発見した。Rust版では`start_track`内でC#の`StopPlayback`に相当する処理を「トランスポート表示のリセット」と「PlaybackController自体の停止」に分離し、`is_preparing_track`フラグを踏み潰さないようにした。結果、Rust版では読み込み中に進捗バーが正しく表示される(C#版より改善された挙動)。

### 今回のフェーズで簡略化した箇所(既知のギャップ)
- **エラーダイアログ**: `DialogService.cs`(独自スタイルのモーダルウィンドウ)は未移植。現状は失敗時にステータス行への表示 + `eprintln!`(標準エラー出力)のみ。UIをブロックする確認ダイアログ(初回セットアップ確認等)はPhase 5のsmp-setup実装時にまとめて対応する。
- **ドラッグ&ドロップ**: 計画通りPhase 5でWindowsネイティブ実装。
- **Discord Rich Presence**: 未結線(Phase 5)。

### 検証状況
- `cargo build -p smp-app` / `cargo clippy --workspace --all-targets` ともに成功・警告ゼロ。
- 実機で `SimpleMusicPlayer.exe "C:\Windows\Media\Alarm01.wav"` を起動し、クラッシュせず4秒間安定動作、エラー出力なしを確認(自動化できるのはここまで)。
- **ボタン操作・キーボードショートカット・ドラッグ操作・履歴のダブルクリック等の対話的なUI検証は、Win32スクリーンショットで実デスクトップの個人的な画面内容を誤って撮影してしまった事故(Phase 3参照)以降この手法を封印したため、自動化できていない。** ユーザー自身による実機確認が必要。

### ユーザー実機テストで発覚した重大バグとその修正
ユーザーに実際に操作してもらったところ、「ボタンを押しても何も反応しない」「URL追加・再生を押してもテキストが変わらず読み込まれているか分からない(ローカル楽曲は普通に再生できた)」という報告があった。

原因: `ui/app-window.slint` の `AppButton`/`DeleteButton`、およびQueue/Recent Albums/Recent Tracksの各行に追加していたダブルクリック検知用`TouchArea`が、**明示的な`width`/`height`を指定していなかった**ため、実質クリック領域を持たず(0サイズ相当)、クリックがほぼ反応しない状態になっていた。CLI引数で渡した初回トラックだけは`start_track`が直接呼ばれるため再生できていたが、ボタン経由の操作(Add/Play-Pauseなど)は`TouchArea`経由のコールバックが発火せず何も起きなかった、という筋が通る。

修正:
- `AppButton`/`DeleteButton`内の`TouchArea`に`width: 100%; height: 100%;`を追加。
- ダブルクリック検知用`TouchArea`は、`QueueRow`/`AlbumHistoryRow`/`TrackHistoryRow`の**外側から子として追加**していたため、削除ボタン(`DeleteButton`)より上のz-orderに乗ってしまい、削除ボタンへのクリックを奪う設計にもなっていた。各行コンポーネント自身に`callback double-clicked()`を持たせ、内部の先頭(z-orderの最下層)に`width:100%; height:100%;`のTouchAreaを配置する形に変更し、削除ボタン自身のクリックが優先されるようにした。

再ビルド・`cargo clippy --workspace --all-targets`・`cargo test --workspace`は全てクリーン。

### 追加フィードバック: ボタンにホバー/プレスの視覚反応が無い
上記のTouchAreaサイズ修正後、ユーザーに再確認してもらったところ、クリック自体は機能する(yt-dlp未導入のためYouTube追加は失敗するが、その失敗自体は起きている)ものの、**`AppButton`にはホバー/プレス時の見た目の変化が全く実装されていなかった**(`background: root.primary ? #3A7AFE : (touch.pressed ? #303030 : #262626);` — プライマリボタン(Add/Play-Pauseなど)はpressed時すら常に同じ色で無反応、非プライマリボタンもpressedのみでhover非対応)。一方Recent系タブはListView標準の行ハイライトがあるため「触った反応がある」ように感じられていた。

修正: `TouchArea`の`has-hover`/`pressed`プロパティ(Slint公式ドキュメントで存在確認済み)を使い、`AppButton`・`DeleteButton`双方にホバー色・プレス色を追加し、`animate background { duration: 80ms; }`で滑らかに遷移するようにした。再ビルド確認済み。

**この修正後の対話的な再確認はまだ完了していない**(ユーザーに再テストを依頼する)。

### 追加フィードバック2件
1. **シークバーで現在位置から離れた場所をクリックすると最初の位置に戻される**: `SeekSlider`の`pointer-event`ハンドラが`PointerEventKind.down`時に`drag-started()`を呼ぶだけで`root.value`をクリック位置から更新しておらず、`moved`(ドラッグ移動)時にしか値を更新していなかった。単純なクリック(ドラッグなし)では`value`が変わらないまま`drag-finished(root.value)`で古い値がRust側に送られ、結果的に再生位置が元に戻っていた。`down`イベント時にも`moved`と同じ計算式で`root.value`を即座に更新するよう修正。
2. **Add横のテキストボックスの入力文字がボックス内で上寄りに表示される**: `AppTextInput`内の`TextInput`/プレースホルダー`Text`が`y: (parent.height - self.height) / 2`という手計算の中央寄せをしており、`TextInput`の`self.height`(自然サイズ)が実際の文字の視覚的な位置と一致していなかった。`height: parent.height;` + `vertical-alignment: center;`(Slint公式ドキュメントで`TextInput`に存在確認済み)に変更し、レイアウトエンジンに中央寄せを任せる形に修正。

再ビルド確認済み。

### 追加フィードバック3: Recentタブの行自体にホバー反応が無い
`QueueRow`/`AlbumHistoryRow`/`TrackHistoryRow`はダブルクリック検知用`TouchArea`を内蔵させたが、その`TouchArea`の`has-hover`状態を見た目(背景色)に一切結びつけていなかった。各行の`background`を`row-touch.has-hover ? #232323 : transparent`にバインドし、`animate background { duration: 80ms; }`を追加。再ビルド確認済み。

**この修正後の対話的な再確認はまだ完了していない**。

### 追加フィードバック4: 処理中の多重クリックで操作が積み上がる(重要な状態管理バグ)
ユーザーから「フォルダ選択中に何度もボタンをクリックすると、処理終了後にクリックした回数分だけ再度開かれる」との報告。原因は`rfd::FileDialog::pick_files()`/`pick_folder()`がUIスレッドをブロックするAPIである一方、ダイアログがOS的に真のモーダルとして親ウィンドウの入力を遮断していない(または遮断されていても、ブロック中にOSキューに溜まった入力がブロック解除後に処理される)ため、ブロック中の追加クリックがハンドラの多重実行を引き起こしていた。

修正(第1弾、不十分だった): `AppState`に`is_busy: bool`を追加し、`open_files`/`add_album`の入り口で`if self.is_busy { return; }`による再入防止ガードを実装。`refresh_ui()`でも`is_busy`中は`Open`/`Add Album`/`Add`/`Loop`/URL入力欄を無効化するようにした。

**→ ユーザー再テストで依然として多重ダイアログが発生**。加えて、ダイアログ表示中に裏のRecentタブへ行ったクリック(ホールド)が、ダイアログを閉じた瞬間に処理され、選択中の項目が変わってしまう症状も報告された。

### 根本原因の再分析
`is_busy`によるアプリ状態ガードは**同一スレッド内で処理が完全にシリアライズされる**ため、実際には無力だった: `pick_folder()`はUIスレッドをブロックするが、ブロック中にOSが溜め込んだクリックは、ブロックが解除された直後に**1件ずつ順番に**Slintのイベントループへ配送される。1件目のクリック(`add_album`)が`is_busy=true`→ダイアログ表示→ダイアログを閉じる→`is_busy=false`まで一気に完了してしまうため、2件目のクリックが配送される時点では既に`is_busy`は`false`に戻っており、ガードをすり抜けて再度ダイアログを開いてしまっていた。同様に、裏のRecentタブへのクリックも同じ理由で「ダイアログを閉じた直後にまとめて配送される」形で処理されていた。

**真因**: `rfd`のダイアログがOSレベルでアプリウィンドウに対して真にモーダル(親子関係)になっておらず、ダイアログ表示中もウィンドウ自体は入力を受け付け続けていたこと。アプリ状態のフラグだけでは、そもそも入力イベントが発生・キューイングされてしまうこと自体を防げない。

### 実際の修正
`slint`クレートに`raw-window-handle-06`featureを有効化し、`ComponentHandle::window().window_handle()`で取得した生ウィンドウハンドルを`rfd::FileDialog::set_parent(&handle)`に渡すことで、ダイアログをOSレベルで真にアプリウィンドウの子(モーダル)にした。これによりダイアログ表示中はOS自体がアプリウィンドウへの入力配送を止めるため、閉じた後の「溜まったクリックの一括処理」が発生しなくなる。

加えて、`is_busy`ガードは防御的多重化として`toggle_playback`/`go_to_previous_track`/`go_to_next_track`/`reverse_queue`/`cycle_loop_mode`/`queue_item_double_clicked`/`replay_album_history`/`replay_track_history`/`remove_album_history`/`remove_track_history`にも展開し、全操作系メソッドで一貫して「処理中は他の操作を受け付けない」を保証するようにした。

再ビルド・`cargo clippy --workspace --all-targets`・`cargo test --workspace`は全てクリーン。

**この修正後の対話的な再確認はまだ完了していない**。

---

## Phase 5 実施結果(進行中)

### Discord Rich Presence — 完了
`crates/smp-discord`を新設し、`discord-rich-presence`クレート(crates.io 1.1.0)で`DiscordPresenceService.cs`を移植。実装時に事前調査と異なっていた点(実機ビルドで発覚):
- `DiscordIpcClient::new(app_id)`は`Result`を返さず`Self`を直接返す(事前のWebFetch調査が誤っていた)。
- 切断は`disconnect()`ではなく`close()`。

`AppState`に`discord: DiscordPresenceService`フィールドを追加し、`PlaybackStarted`/`toggle_playback`/`seek_by`/`seek_drag_finished`で`update_discord_presence()`を、`continue_after_media_ended`の再生終了時・`handle_playback_failure`・`stop_playback`で`discord.clear()`を呼ぶよう結線。ビルド・clippy・テスト全てクリーン。

### Windowsネイティブドラッグ&ドロップ — 完了(実機での見た目確認は未了)
`crates/smp-app/src/windows_drop_target.rs`に`windows`クレート(0.62、`raw-window-handle-06`経由でSlintウィンドウの実HWNDを取得)で`IDropTarget`をCOM実装し、`RegisterDragDrop`でウィンドウに登録。実装は`#[implement(IDropTarget)]`マクロを使い、Zed(zed-industries/zed)の実装例をリファレンスにして`impl IDropTarget_Impl for DropHandler_Impl`のパターンを確認した。

**実装中に発覚した2つの非自明な問題(いずれも実機ビルド+ログで特定):**

1. **タイミング問題**: `window.show()`直後に登録すると、Slintの`Window::window_handle()`が`raw_window_handle::HandleError::Unavailable`相当を返し(winitの`WinitWindowAdapter::window_handle_06_rc()`がまだ内部winitウィンドウを持っていないため)、最終的に`NotSupported`として観測される。Slint公式ドキュメントの「show()後の最低1イベントループ後」という記述だけでは不十分だったため、`Timer::single_shot`による50ms間隔・最大20回のリトライ方式に変更して解消。
2. **登録の衝突**: winit自身のWin32バックエンドが、winit独自のクロスプラットフォーム`DroppedFile`イベント用に**既にそのHWNDへ`IDropTarget`を登録済み**だった(winitソース`platform_impl/windows/drop_handler.rs`で確認)。Windowsは1ウィンドウにつき1つのドロップターゲットしか許可しないため、自前の`RegisterDragDrop`は`DRAGDROP_E_ALREADYREGISTERED (0x80040101)`で失敗し続けていた。**`RegisterDragDrop`の前に`RevokeDragDrop(hwnd)`でwinit側の登録を解除**することで解消(SlintはwinitのDroppedFileイベントを`.slint`側に転送していない— slint-ui/slint#7328 で追跡中の既知の未実装機能 — なので実質失うものはない)。

ビルド・clippy・テストは全てクリーンで、起動時にエラーなく登録が完了することをログで確認済み。**ただし実際にExplorerからファイル/フォルダをドラッグ&ドロップして期待通りキューに追加されるかは、対話操作が必要なため未確認。ユーザーに確認を依頼する。**

次: smp-setup(Windowsインストーラ/アンインストーラのネイティブ化)。

### smp-setup — 完了(実機での統合検証は未了)
`crates/smp-setup`を新設し、PowerShell群(`Install-Release.ps1`/`Uninstall-Release.ps1`/`modules/*.psm1`/`install-config.psd1`)の全機能をネイティブRustで再実装(ユーザー確認済みスコープ: 全部入り):

- `config.rs` — install-config.psd1の定数(アプリ名/ProgId/ツールURL等)。対応拡張子はsmp-coreから取得しドリフト防止。
- `shortcut.rs` — `IShellLinkW`/`IPersistFile` COM(`CoCreateInstance(&ShellLink,...)`)でStart Menuショートカット作成/削除。API署名はローカルのwindows-0.62.2クレートソースをgrepして確認(外部サンプル取得が不安定だったため、ソース直接確認に切り替えた)。
- `path_env.rs` — `winreg`で`HKCU\Environment`のPath追加/削除 + `SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, ..., "Environment")`ブロードキャスト(計画時に注意点として記録していた.NET内部挙動の明示的再現)。
- `shell_integration.rs` — ShellIntegration.psm1の全レジストリキーを1:1移植(Directory\shell、Background\shell、ProgId、Applications、Capabilities+FileAssociations、RegisteredApplications、OpenWithProgids、Uninstallキー)。`looks_configured()`(AppSetupCoordinator.LooksConfigured相当)も移植。
- `tools.rs` — `reqwest`(blocking)+`zip`でyt-dlp(直接exe)/deno(zip展開)/ffmpeg(zip→binディレクトリ探索→ffmpeg/ffprobe/ffplayコピー)のダウンロード。既存ならスキップ、`redownload`で強制再取得。
- `lib.rs` — `should_offer_setup`/`mark_dismissed`/`run_setup`/`run_uninstall`。非Windowsは全てno-opスタブ。

**意図的な差分(記録):**
1. フォルダ背景の右クリックメニューのコマンドは、旧版が隠しPowerShell経由で作業ディレクトリを設定していたのに対し、ネイティブ版は`"exe" --album "%V"`に変更(PowerShell依存排除、実効的に同じ「このフォルダを再生」)。
2. `should_offer_setup`の「配布物であること」の判定は、旧版の「Install-SimpleMusicPlayer.ps1がexeの隣に存在するか」に代えて「リリースビルドであるか」(`cfg!(debug_assertions)`で開発ビルドを除外)+exe名チェックに変更。
3. アンインストールはps1スクリプトではなく`"exe" --uninstall [--remove-user-data --remove-cache --remove-bundled-tools --remove-app-directory]`をバイナリ自身が処理(スイッチ名は旧スクリプトを踏襲)。`--remove-app-directory`の自己削除は旧版の遅延PowerShellに代えてdetachedな`cmd /C ping…& rmdir`で再現。

**smp-app統合:**
- `.slint`にモーダルダイアログオーバーレイを追加(DialogService.cs相当。確認/情報両対応、キャンセルテキスト空で情報ボックス化)。Phase 4で省略していた再生エラーダイアログもこれで実装済み(`handle_playback_failure`が使用)。
- `app_state.rs`に`PendingDialog`(None/Info/SetupOffer)、`offer_app_setup_if_needed`(起動300ms後にTimerで呼出)、`dialog_confirmed`/`dialog_cancelled`、`run_app_setup`(`spawn_blocking`でネットワーク/レジストリ処理をワーカーに逃がし、完了後にyt-dlp/ffmpegキャッシュを再生成=C#のReloadExternalToolCaches相当)を実装。
- `main.rs`に`--uninstall`のコンソール処理(UI起動前にexit)を追加。

ビルド・clippy(警告ゼロ)・全50テストパス・起動スモークテスト(エラー出力なし)確認済み。**セットアップの実フロー(ダイアログ→ショートカット/レジストリ/ツールDL→アンインストール)はリリースビルド+実際の配布フォルダでの実機検証が必要**(debugビルドでは`should_offer_setup`が意図的に常時false)。Phase 6のパッケージング後にまとめて検証する。

Phase 5 完了。次: Phase 6(パッケージング・CI更新)。

---

## Phase 6 実施結果 [完了]

### ローカル開発インフラ
- 旧C#ビルド出力(`obj/verify-publish/libvlc/win-x64`)にあったlibvlcランタイムを`vendor/libvlc/win-x64`へ移設(gitignore対象)。`scripts/fetch-libvlc.ps1`を新設し、VideoLAN.LibVLC.Windows NuGetパッケージ(3.0.21)から再取得可能にした。
- `.cargo/config.toml`に`[env] VLC_LIB_DIR = { value = "vendor/libvlc/win-x64", relative = true }`を追加。**手動での環境変数exportが不要になった**(cargoが依存クレートのビルドスクリプトにも伝播することをドキュメントで確認済み+実ビルドで検証済み)。

### アイコン
- `winresource`(0.1.31)でexeに`Assets/app-icon.ico`を埋め込み(旧csprojの`ApplicationIcon`相当)。
- Slintの`Window.icon`に`Assets/app-icon.png`を指定(タスクバー/タイトルバー用)。

### CI(`.github/workflows/release.yml`)
- `dotnet publish`マトリクスを`cargo build --release --locked`の2ジョブ(win-x64 / linux-x64)に置き換え。Windowsジョブは`fetch-libvlc.ps1`→ビルド→テスト→`package-release.ps1 -SkipBuild`→zip→Release添付。Linuxは`apt install libvlc-dev vlc`+システムlibvlcリンク(バイナリのみ配布、従来と同じ「システムvlc前提」)。
- **macOSターゲット(osx-x64/osx-arm64)は今回のCI対象から除外**。vlc-rsが非WindowsでpkgConfig必須だが、macOS CIランナーでlibvlcの.pcを用意する確立された方法がなく、このマシンから検証不能なため。READMEに「ソースビルドのみ・バイナリ配布は準備中」と明記(旧版からの機能後退として記録)。CIは未実行のため動作未検証(タグpush時に要確認)。

### パッケージング
- `scripts/package-release.ps1`新設(旧`Publish-Release.ps1`置き換え)。`cargo build --release`+exe/libvlc.dll/libvlccore.dll/plugins/hrtfs/luaのコピーのみ(インストーラロジックはバイナリ内蔵のため不要)。実行して`publish/SimpleMusicPlayer-win-x64/`の生成とパッケージ版exeの起動(エラーなし)を確認済み。リリースexeは約22MB。

### C#プロジェクト削除
全`.cs`/`.axaml`/`.csproj`、旧PowerShellスクリプト(`Install-Release.ps1`/`Publish-Release.ps1`/`Uninstall-Release.ps1`/`install-config.psd1`/`modules/`)、旧ビルド出力(`obj/`/`bin/`/`release/`/旧publish内容)を削除。すべてgit管理下の未変更ファイルだったため`git restore`で復元可能。`Assets/`(アイコン)は継続使用のため残置。削除後に`cargo build --workspace`+全テストがパスすることを確認済み。

### README
Rust版の内容に全面更新(ビルド手順・fetch-libvlc・workspace構成・`--uninstall`スイッチ・macOS制限)。

### Phase 6 後の追加対応(2026-07-10)
1. **旧C#版のクリーンアンインストール**: `D:\Tools\SimpleMusicPlayer`(旧版インストール先)についてユーザーから「ps1が上手くいかなかった」と依頼。調査の結果、レジストリ・PATH・ショートカットは既に削除済みで、残っていたのはアプリフォルダ本体と`setup-state.json`のみだった。フォルダ内`tools/`(DL済みyt-dlp/deno/ffmpeg)を`publish/SimpleMusicPlayer-win-x64/tools/`へ退避してからフォルダを削除し、stale な`setup-state.json`も削除。`history.json`は新版が引き継ぐため残置。**これによりyt-dlpが使える状態になり、未検証だったURL再生の実機テストが可能になった。**
2. **起動時にコンソールウィンドウが出る問題**: Rustバイナリのデフォルトがコンソールサブシステムのため(C#の`WinExe`相当が未設定だった)。`#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]`をmain.rsに追加(releaseのみGUI化、debugは開発用にコンソール維持)。`--uninstall`実行時は`AttachConsole(ATTACH_PARENT_PROCESS)`で親ターミナルに再接続し結果表示を維持。PEヘッダーのSubsystem=2(GUI)をバイナリ解析で確認済み。publishフォルダのexeも更新済み。

### 残タスク(ユーザー実機確認待ち)
1. リリースパッケージ(`publish/SimpleMusicPlayer-win-x64/`)での初回セットアップフロー(ダイアログ→ショートカット/PATH/レジストリ/ツールDL→アンインストール)の実機確認。
2. yt-dlp導入後のURL再生・YouTubeプレイリストの実機確認(開発機にyt-dlpが無いため一度も実URLで検証できていない。セットアップのツールDLを実行すれば揃う)。
3. タグpush時のCI動作確認(特にLinuxジョブとWindowsジョブのfetch-libvlc)。
4. Discord Rich Presenceの実機確認(Discordクライアント起動状態での表示)。
5. コミットはまだ何も作成していない(全変更がworking treeにある状態)。(Discord Presence / Windowsネイティブドラッグ&ドロップ / smp-setup)。
