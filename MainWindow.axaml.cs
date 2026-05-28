using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace SimpleMusicPlayer;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _positionTimer;
    private readonly MainWindowViewModel _viewModel = new();
    private readonly AppSetupCoordinator _appSetupCoordinator;
    private readonly DiscordPresenceService _discordPresence;
    private readonly PlaybackHistoryService _historyService;
    private readonly PlaybackSourceCollector _sourceCollector = new();
    private readonly ObservableCollection<AlbumHistoryEntry> _albumHistory;
    private readonly ObservableCollection<PlaybackItem> _queue;
    private readonly ObservableCollection<TrackHistoryEntry> _trackHistory;
    private readonly Random _random = new();
    private readonly Slider _seekSlider;
    private FfmpegAudioCache _ffmpegAudioCache;
    private YtDlpAudioCache _ytDlpAudioCache;
    private PlaybackController? _playbackController;
    private CancellationTokenSource? _trackLoadCts;
    private bool _hasCheckedAppSetup;
    private bool _isDraggingSeekBar;
    private bool _isRunningAppSetup;
    private bool _isPlaying;
    private bool _isMediaLoaded;
    private bool _isPreparingTrack;
    private int _currentIndex = -1;
    private LoopMode _loopMode = LoopMode.All;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _albumHistory = _viewModel.AlbumHistory;
        _queue = _viewModel.Queue;
        _trackHistory = _viewModel.TrackHistory;
        ConfigureCommands();

        _seekSlider = this.FindControl<Slider>("SeekSlider")!;
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _positionTimer.Tick += (_, _) => UpdateSeekUi();

        _seekSlider.PropertyChanged += SeekSlider_PropertyChanged;
        _seekSlider.AddHandler(InputElement.PointerPressedEvent, SeekSlider_PointerPressed, RoutingStrategies.Tunnel);
        _seekSlider.AddHandler(InputElement.PointerReleasedEvent, SeekSlider_PointerReleased, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        Closing += (_, _) => DisposeResources();

        _appSetupCoordinator = new AppSetupCoordinator();
        var options = CliOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _discordPresence = new DiscordPresenceService();
        _ffmpegAudioCache = new FfmpegAudioCache();
        _historyService = new PlaybackHistoryService();
        _ytDlpAudioCache = new YtDlpAudioCache();
        _loopMode = options.LoopMode;

        LoadHistory();
        UpdateLoopButton();

        var initialSources = ResolveInitialSources(options);
        if (initialSources.Count > 0)
        {
            var added = AddSources(initialSources, append: false, options.Shuffle);
            if (added.Count > 0)
            {
                var startIndex = Math.Clamp(options.StartIndex ?? 0, 0, added.Count - 1);
                _ = StartTrackAsync(startIndex);
            }
        }

        UpdateUiState();
        Dispatcher.UIThread.Post(async () => await OfferAppSetupIfNeededAsync(), DispatcherPriority.Background);
    }

    private void ConfigureCommands()
    {
        _viewModel.OpenCommand = new AsyncRelayCommand(_ => OpenFilesAsync());
        _viewModel.AddAlbumCommand = new AsyncRelayCommand(_ => AddAlbumAsync());
        _viewModel.ReverseQueueCommand = new RelayCommand(_ => ReverseQueueOrder());
        _viewModel.PreviousCommand = new AsyncRelayCommand(_ => GoToPreviousTrackAsync());
        _viewModel.PlayPauseCommand = new AsyncRelayCommand(_ => TogglePlaybackAsync());
        _viewModel.NextCommand = new AsyncRelayCommand(_ => GoToNextTrackAsync());
        _viewModel.LoopCommand = new RelayCommand(_ => CycleLoopMode());
        _viewModel.AddSourceCommand = new AsyncRelayCommand(_ => AddSourceFromInputAsync());
        _viewModel.RemoveAlbumHistoryCommand = new RelayCommand(RemoveAlbumHistory);
        _viewModel.RemoveTrackHistoryCommand = new RelayCommand(RemoveTrackHistory);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Space)
        {
            await TogglePlaybackAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await GoToPreviousTrackAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await GoToNextTrackAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            SeekBy(TimeSpan.FromSeconds(-5));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            SeekBy(TimeSpan.FromSeconds(5));
            e.Handled = true;
        }
    }

    private async Task OpenFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Select audio or video files",
            FileTypeFilter =
            [
                new FilePickerFileType("Media files")
                {
                    Patterns = [.. MediaFileTypes.SupportedPatterns]
                }
            ]
        });

        var added = AddSources(ResolveStoragePaths(files), append: false, shuffle: false);
        if (added.Count > 0)
        {
            await StartTrackAsync(0);
        }
    }

    private async Task AddAlbumAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select an album folder to append to the queue"
        });

        var added = AddSources(ResolveStoragePaths(folders), append: true, shuffle: false);
        if (added.Count > 0 && _currentIndex < 0)
        {
            await StartTrackAsync(0);
        }
    }

    private void CycleLoopMode()
    {
        _loopMode = _loopMode.Next();

        UpdateLoopButton();
        UpdateUiState();
    }

    private void SeekSlider_PointerPressed(object? sender, PointerPressedEventArgs e) => _isDraggingSeekBar = true;

    private void SeekSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDraggingSeekBar = false;
        ApplySeekFromSlider();
    }

    private void SeekSlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty || !_isDraggingSeekBar)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(_viewModel.SeekValue);
        _viewModel.ElapsedText = FormatTime(target);
    }

    private async void SourceInputTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        await AddSourceFromInputAsync();
        e.Handled = true;
    }

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void AlbumHistoryList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel.SelectedAlbumHistory is AlbumHistoryEntry entry)
        {
            await ReplayAlbumHistoryAsync(entry);
        }
    }

    private async void TrackHistoryList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel.SelectedTrackHistory is TrackHistoryEntry entry)
        {
            await ReplayTrackHistoryAsync(entry);
        }
    }

    private void RemoveAlbumHistory(object? parameter)
    {
        if (parameter is AlbumHistoryEntry entry &&
            _historyService.RemoveAlbum(_albumHistory, _trackHistory, entry))
        {
            SetStatus("Removed album from history.");
            UpdateUiState();
        }
    }

    private void RemoveTrackHistory(object? parameter)
    {
        if (parameter is TrackHistoryEntry entry &&
            _historyService.RemoveTrack(_albumHistory, _trackHistory, entry))
        {
            SetStatus("Removed track from history.");
            UpdateUiState();
        }
    }

    private async void QueueList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel.SelectedQueueItem is not PlaybackItem item)
        {
            return;
        }

        var index = _queue.IndexOf(item);
        if (index >= 0)
        {
            await StartTrackAsync(index);
        }
    }

    private async void Window_Drop(object? sender, DragEventArgs e)
    {
        var droppedItems = e.DataTransfer.TryGetFiles();
        if (droppedItems is null)
        {
            return;
        }

        var droppedPaths = ResolveStoragePaths(droppedItems);
        if (droppedPaths.Count == 0)
        {
            return;
        }

        var append = _queue.Count > 0 && droppedPaths.Any(Directory.Exists);
        var added = AddSources(droppedPaths, append, shuffle: false);
        if (added.Count > 0 && (_currentIndex < 0 || !append))
        {
            await StartTrackAsync(append ? _queue.Count - added.Count : 0);
        }
    }

    private List<PlaybackItem> AddSources(IEnumerable<string> rawSources, bool append, bool shuffle)
    {
        if (!append)
        {
            CancelTrackPreparation();
            StopPlayback(clearSource: true);
            _queue.Clear();
        }

        var collected = _sourceCollector.Collect(
            rawSources,
            _ytDlpAudioCache.IsSupportedUrl,
            shuffle,
            _random);

        foreach (var album in collected.Albums)
        {
            RecordAlbumHistory(album.Path, album.TrackCount);
        }

        foreach (var item in collected.Items)
        {
            _queue.Add(item);
        }

        _currentIndex = _queue.Count == 0
            ? -1
            : append
                ? _currentIndex
                : 0;

        UpdateUiState();
        return [.. collected.Items];
    }

    private async Task StartTrackAsync(int index)
    {
        if (index < 0 || index >= _queue.Count)
        {
            return;
        }

        var currentLoad = BeginTrackPreparation();
        var cancellationToken = currentLoad.Token;
        var item = _queue[index];

        _currentIndex = index;
        _isPreparingTrack = true;
        _isMediaLoaded = false;
        _isPlaying = false;

        _positionTimer.Stop();
        ResetTransportUi();
        StopPlayback(clearSource: true);

        _viewModel.TrackTitle = item.DisplayName;
        _viewModel.QueueInfo = MainWindowViewModel.BuildQueueText(item, _currentIndex, _queue.Count);
        _viewModel.SelectedQueueItem = item;
        SetStatus(BuildPreparationStatus(item));
        UpdateUiState();

        try
        {
            var playbackPath = await ResolvePlaybackPathAsync(item, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var player = EnsurePlaybackController();
            if (!player.Play(playbackPath))
            {
                throw new InvalidOperationException("Could not start playback with LibVLC.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await HandlePlaybackFailureAsync(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_trackLoadCts, currentLoad))
            {
                _trackLoadCts = null;

                if (!_isMediaLoaded)
                {
                    _isPreparingTrack = false;
                    UpdateUiState();
                }

                currentLoad.Dispose();
            }
        }
    }

    private async Task TogglePlaybackAsync()
    {
        if (_queue.Count == 0 || _isPreparingTrack)
        {
            return;
        }

        if (_currentIndex < 0)
        {
            await StartTrackAsync(0);
            return;
        }

        if (!_isMediaLoaded)
        {
            await StartTrackAsync(_currentIndex);
            return;
        }

        var player = EnsurePlaybackController();
        if (_isPlaying)
        {
            player.Pause();
            _positionTimer.Stop();
            _isPlaying = false;
        }
        else
        {
            player.Resume();
            _positionTimer.Start();
            _isPlaying = true;
        }

        UpdateDiscordPresence();
        UpdateUiState();
    }

    private async Task GoToPreviousTrackAsync()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        if (_isMediaLoaded && _playbackController is not null && _playbackController.Position > TimeSpan.FromSeconds(3))
        {
            _playbackController.Position = TimeSpan.Zero;
            UpdateSeekUi();
            return;
        }

        if (_currentIndex < 0)
        {
            await StartTrackAsync(0);
            return;
        }

        var previousIndex = PlaybackQueueNavigator.GetPreviousIndex(_currentIndex, _queue.Count, _loopMode);
        await StartTrackAsync(previousIndex);
    }

    private async Task GoToNextTrackAsync()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        if (_currentIndex < 0)
        {
            await StartTrackAsync(0);
            return;
        }

        var nextIndex = PlaybackQueueNavigator.GetNextIndex(_currentIndex, _queue.Count, _loopMode);
        if (nextIndex < 0)
        {
            return;
        }

        await StartTrackAsync(nextIndex);
    }

    private void ReverseQueueOrder()
    {
        if (_queue.Count < 2 || _isPreparingTrack)
        {
            return;
        }

        var currentItem = _currentIndex >= 0 && _currentIndex < _queue.Count
            ? _queue[_currentIndex]
            : null;
        var reversedItems = _queue.Reverse().ToList();

        _queue.Clear();
        foreach (var item in reversedItems)
        {
            _queue.Add(item);
        }

        _currentIndex = currentItem is null ? -1 : _queue.IndexOf(currentItem);
        _viewModel.SelectedQueueItem = currentItem;
        SetStatus("Queue order reversed.");
        UpdateUiState();
    }

    private void SeekBy(TimeSpan offset)
    {
        if (!_isMediaLoaded || _playbackController is null || _playbackController.Duration <= TimeSpan.Zero)
        {
            return;
        }

        var duration = _playbackController.Duration;
        var nextPosition = _playbackController.Position + offset;
        nextPosition = TimeSpan.FromSeconds(Math.Clamp(nextPosition.TotalSeconds, 0, duration.TotalSeconds));
        _playbackController.Position = nextPosition;
        UpdateSeekUi();
    }

    private void ApplySeekFromSlider()
    {
        if (!_isMediaLoaded || _playbackController is null || _playbackController.Duration <= TimeSpan.Zero)
        {
            return;
        }

        _playbackController.Position = TimeSpan.FromSeconds(_viewModel.SeekValue);
        UpdateSeekUi();
    }

    private void UpdateSeekUi()
    {
        if (!_isMediaLoaded || _playbackController is null || _playbackController.Duration <= TimeSpan.Zero)
        {
            _viewModel.ElapsedText = "00:00";
            _viewModel.RemainingText = "00:00";
            _viewModel.SeekValue = 0;
            _viewModel.SeekMaximum = 1;
            return;
        }

        var duration = _playbackController.Duration;
        var position = _playbackController.Position;

        if (!_isDraggingSeekBar)
        {
            _viewModel.SeekMaximum = Math.Max(duration.TotalSeconds, 1);
            _viewModel.SeekValue = Math.Clamp(position.TotalSeconds, 0, _viewModel.SeekMaximum);
        }

        _viewModel.ElapsedText = FormatTime(position);
        _viewModel.RemainingText = $"-{FormatTime(duration - position)}";
    }

    private void UpdateUiState()
    {
        _viewModel.ApplyPlaybackState(
            _queue,
            _currentIndex,
            _isPreparingTrack,
            _isPlaying,
            _isMediaLoaded,
            _isRunningAppSetup);
    }

    private void UpdateLoopButton()
    {
        var loopText = _loopMode.DisplayText();

        _viewModel.LoopButtonContent = loopText.Replace("Loop ", "Loop: ");
        _viewModel.LoopModeBadge = loopText;
    }

    private static IReadOnlyCollection<string> ResolveInitialSources(CliOptions options)
    {
        if (options.Sources.Count > 0)
        {
            return options.Sources;
        }

        var currentDirectory = Environment.CurrentDirectory;
        if (Directory.Exists(currentDirectory) &&
            Directory.EnumerateFiles(currentDirectory).Any(IsSupportedMediaFile))
        {
            return [currentDirectory];
        }

        return Array.Empty<string>();
    }

    private void UpdateDiscordPresence()
    {
        if (_currentIndex < 0 || _currentIndex >= _queue.Count)
        {
            _discordPresence.Clear();
            return;
        }

        _discordPresence.SetNowPlaying(_queue[_currentIndex], _currentIndex, _queue.Count, _isPlaying);
    }

    private static bool IsSupportedMediaFile(string path)
        => MediaFileTypes.IsSupported(path);

    private async Task HandlePlaybackFailureAsync(Exception? exception)
    {
        _positionTimer.Stop();
        _isPreparingTrack = false;
        _isMediaLoaded = false;
        _isPlaying = false;
        _discordPresence.Clear();
        SetStatus("Playback failed.");
        UpdateUiState();

        var message = exception?.Message ?? "Unknown playback error.";
        if (_currentIndex >= 0 && _currentIndex < _queue.Count)
        {
            var item = _queue[_currentIndex];
            if (_ytDlpAudioCache.IsSupportedUrl(item.Path) && !_ytDlpAudioCache.IsAvailable)
            {
                message = "yt-dlp was not found in PATH or the bundled tools directory, so this URL could not be fetched.";
            }
            else if (MediaFileTypes.RequiresTranscode(item.Path) && !_ffmpegAudioCache.IsAvailable)
            {
                message = "ffmpeg was not found in PATH or the bundled tools directory, so this file could not be decoded.";
            }
        }

        await DialogService.ShowInfoAsync(this, "Playback error", $"Could not play media.\n{message}");
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private async Task AddSourceFromInputAsync()
    {
        var source = NormalizeSourceInput(_viewModel.SourceInput);
        if (!TryResolveInputSource(source, out var resolvedSource, out var validationMessage))
        {
            await DialogService.ShowInfoAsync(this, "Invalid source", validationMessage);
            return;
        }

        List<PlaybackItem> added;
        try
        {
            added = _ytDlpAudioCache.IsYouTubePlaylistPageUrl(resolvedSource)
                ? await AddYouTubePlaylistSourceAsync(resolvedSource, append: _queue.Count > 0)
                : AddSources([resolvedSource], append: _queue.Count > 0, shuffle: false);
        }
        catch (Exception ex)
        {
            SetStatus("Could not add source.");
            await DialogService.ShowInfoAsync(this, "Add source error", $"Could not add this source.\n{ex.Message}");
            return;
        }

        _viewModel.SourceInput = string.Empty;

        if (added.Count > 0 && _currentIndex < 0)
        {
            await StartTrackAsync(0);
            return;
        }

        if (added.Count > 0 && !_isPlaying && _queue.Count == added.Count)
        {
            await StartTrackAsync(0);
        }
    }

    private async Task<List<PlaybackItem>> AddYouTubePlaylistSourceAsync(string playlistUrl, bool append)
    {
        SetStatus("Fetching playlist...");
        UpdateUiState();

        var playlist = await _ytDlpAudioCache.GetPlaylistAsync(playlistUrl, CancellationToken.None);
        var addedItems = playlist.Entries
            .Select(entry => PlaybackItem.FromPlaylistTrack(entry.Url, playlist.Title, entry.Title))
            .ToList();

        if (!append)
        {
            CancelTrackPreparation();
            StopPlayback(clearSource: true);
            _queue.Clear();
            _currentIndex = -1;
        }

        foreach (var item in addedItems)
        {
            _queue.Add(item);
        }

        _currentIndex = _queue.Count == 0
            ? -1
            : append
                ? _currentIndex
                : 0;

        RecordAlbumHistory(playlist.Url, addedItems.Count, playlist.Title);
        SetStatus(addedItems.Count == 0 ? "Playlist is empty." : "Playlist added.");
        UpdateUiState();
        return addedItems;
    }

    private async Task OfferAppSetupIfNeededAsync()
    {
        if (_hasCheckedAppSetup)
        {
            return;
        }

        _hasCheckedAppSetup = true;
        if (!_appSetupCoordinator.ShouldOfferSetup())
        {
            return;
        }

        var message =
            "Simple Music Player can finish setting itself up for this folder." +
            "\n\nIt will add a Start Menu shortcut, Explorer menu entries, file association hints, and download optional playback tools if they are missing." +
            "\n\nRun setup now?";

        var shouldRunSetup = await DialogService.ShowConfirmationAsync(
            this,
            "Set up Simple Music Player",
            message,
            "Run setup",
            "Later");

        if (!shouldRunSetup)
        {
            _appSetupCoordinator.MarkDismissed();
            return;
        }

        _isRunningAppSetup = true;
        SetStatus("Running first-time setup...");
        UpdateUiState();

        AppSetupRunResult result;
        try
        {
            result = await _appSetupCoordinator.RunSetupAsync(redownloadTools: false, CancellationToken.None);
        }
        finally
        {
            _isRunningAppSetup = false;
            ReloadExternalToolCaches();
            UpdateUiState();
        }

        if (result.Success)
        {
            SetStatus("Setup complete.");
            await DialogService.ShowInfoAsync(this, "Setup complete", "Setup finished. Shortcuts and optional tools are ready for this app folder.");
            return;
        }

        SetStatus("Setup incomplete.");
        await DialogService.ShowInfoAsync(this, "Setup error", $"Setup could not finish.\n{result.Message}");
    }

    private async Task<string> ResolvePlaybackPathAsync(PlaybackItem item, CancellationToken cancellationToken)
    {
        if (_ytDlpAudioCache.IsSupportedUrl(item.Path))
        {
            SetStatus("Fetching audio from URL...");
            var cachedAudio = await _ytDlpAudioCache.GetOrDownloadAsync(item.Path, cancellationToken);
            if (_ffmpegAudioCache.RequiresTranscode(cachedAudio.FilePath) && !_ffmpegAudioCache.IsAvailable)
            {
                throw new InvalidOperationException("ffmpeg was not found in PATH or the bundled tools directory, so this downloaded audio could not be decoded.");
            }

            item.UpdateDisplayName(cachedAudio.Title);
            _viewModel.TrackTitle = item.DisplayName;
            _viewModel.QueueInfo = cachedAudio.WasCached
                ? $"URL {_currentIndex + 1}/{_queue.Count}  cache ready  {cachedAudio.SourceUrl}"
                : $"URL {_currentIndex + 1}/{_queue.Count}  downloaded  {cachedAudio.SourceUrl}";

            if (_ffmpegAudioCache.RequiresTranscode(cachedAudio.FilePath))
            {
                SetStatus(cachedAudio.WasCached
                    ? "Preparing cached audio for playback..."
                    : "Converting downloaded audio for playback...");
            }
            else
            {
                SetStatus(cachedAudio.WasCached ? "Loaded from cache." : "Download complete.");
            }

            return await _ffmpegAudioCache.GetPlaybackPathAsync(cachedAudio.FilePath, cancellationToken);
        }

        if (_ffmpegAudioCache.RequiresTranscode(item.Path))
        {
            if (!_ffmpegAudioCache.IsAvailable)
            {
                throw new InvalidOperationException("ffmpeg was not found in PATH or the bundled tools directory, so this file could not be decoded.");
            }

            SetStatus("Converting this source for reliable playback...");
        }
        else
        {
            SetStatus(BuildCueStatus(item));
        }

        return await _ffmpegAudioCache.GetPlaybackPathAsync(item.Path, cancellationToken);
    }

    private void LoadHistory()
    {
        _historyService.Load(_albumHistory, _trackHistory);
    }

    private void RecordAlbumHistory(string albumPath, int trackCount, string? displayNameOverride = null)
    {
        _historyService.RecordAlbum(_albumHistory, _trackHistory, albumPath, trackCount, displayNameOverride);
    }

    private void RecordTrackHistory()
    {
        if (_currentIndex < 0 || _currentIndex >= _queue.Count)
        {
            return;
        }

        _historyService.RecordTrack(_albumHistory, _trackHistory, _queue[_currentIndex]);
    }

    private async Task ReplayAlbumHistoryAsync(AlbumHistoryEntry entry)
    {
        if (_ytDlpAudioCache.IsYouTubePlaylistPageUrl(entry.AlbumPath))
        {
            List<PlaybackItem> playlistItems;
            try
            {
                playlistItems = await AddYouTubePlaylistSourceAsync(entry.AlbumPath, append: false);
            }
            catch (Exception ex)
            {
                SetStatus("Could not add source.");
                await DialogService.ShowInfoAsync(this, "History", $"Playlist could not be loaded.\n{ex.Message}");
                return;
            }

            if (playlistItems.Count > 0)
            {
                await StartTrackAsync(0);
            }

            return;
        }

        if (!Directory.Exists(entry.AlbumPath))
        {
            await DialogService.ShowInfoAsync(this, "History", "Album folder was not found.");
            return;
        }

        var added = AddSources([entry.AlbumPath], append: false, shuffle: false);
        if (added.Count > 0)
        {
            await StartTrackAsync(0);
        }
    }

    private async Task ReplayTrackHistoryAsync(TrackHistoryEntry entry)
    {
        if (_ytDlpAudioCache.IsSupportedUrl(entry.SourcePath))
        {
            var added = AddSources([entry.SourcePath], append: false, shuffle: false);
            if (added.Count > 0)
            {
                await StartTrackAsync(0);
            }

            return;
        }

        if (!File.Exists(entry.SourcePath))
        {
            await DialogService.ShowInfoAsync(this, "History", "Track file was not found.");
            return;
        }

        var replayed = AddSources([entry.SourcePath], append: false, shuffle: false);
        if (replayed.Count > 0)
        {
            await StartTrackAsync(0);
        }
    }

    private CancellationTokenSource BeginTrackPreparation()
    {
        var previousLoad = _trackLoadCts;
        _trackLoadCts = new CancellationTokenSource();

        if (previousLoad is not null)
        {
            try
            {
                previousLoad.Cancel();
            }
            finally
            {
                previousLoad.Dispose();
            }
        }

        return _trackLoadCts;
    }

    private void CancelTrackPreparation()
    {
        var activeLoad = _trackLoadCts;
        _trackLoadCts = null;
        if (activeLoad is null)
        {
            return;
        }

        try
        {
            activeLoad.Cancel();
        }
        finally
        {
            activeLoad.Dispose();
        }
    }

    private void StopPlayback(bool clearSource)
    {
        _positionTimer.Stop();
        _isDraggingSeekBar = false;
        _isPreparingTrack = false;
        _isMediaLoaded = false;
        _isPlaying = false;

        try
        {
            _playbackController?.Stop(clearSource);
        }
        catch
        {
        }

        ResetTransportUi();
        _discordPresence.Clear();
    }

    private void ResetTransportUi()
    {
        _viewModel.SeekValue = 0;
        _viewModel.SeekMaximum = 1;
        _viewModel.ElapsedText = "00:00";
        _viewModel.RemainingText = "00:00";
    }

    private void SetStatus(string message)
    {
        _viewModel.Status = message;
    }

    private string BuildPreparationStatus(PlaybackItem item)
        => item.IsUrlSource
            ? "Preparing URL source..."
            : item.IsAlbumSource
                ? "Preparing album track..."
                : "Preparing track...";

    private static string BuildCueStatus(PlaybackItem item)
        => item.Kind switch
        {
            PlaybackSourceKind.AlbumTrack => "Cueing album track...",
            PlaybackSourceKind.PlaylistTrack => "Cueing playlist track...",
            _ => "Cueing track..."
        };

    private void ReloadExternalToolCaches()
    {
        _ffmpegAudioCache.Dispose();
        _ffmpegAudioCache = new FfmpegAudioCache();
        _ytDlpAudioCache = new YtDlpAudioCache();
    }

    private static string NormalizeSourceInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return input.Trim().Trim('"');
    }

    private bool TryResolveInputSource(string source, out string resolvedSource, out string validationMessage)
    {
        resolvedSource = source;

        if (string.IsNullOrWhiteSpace(source))
        {
            validationMessage = "Enter a URL, file path, or folder path.";
            return false;
        }

        if (_ytDlpAudioCache.IsSupportedUrl(source))
        {
            validationMessage = string.Empty;
            return true;
        }

        if (Directory.Exists(source))
        {
            validationMessage = string.Empty;
            return true;
        }

        if (File.Exists(source))
        {
            if (IsSupportedMediaFile(source))
            {
                validationMessage = string.Empty;
                return true;
            }

            validationMessage = "That file exists, but its extension is not supported for playback.";
            return false;
        }

        validationMessage = "Enter a valid URL, existing file path, or existing folder path.";
        return false;
    }

    private async Task ContinueAfterMediaEndedAsync()
    {
        _positionTimer.Stop();

        if (_loopMode == LoopMode.One)
        {
            await StartTrackAsync(_currentIndex);
            return;
        }

        var nextIndex = PlaybackQueueNavigator.GetNextIndex(_currentIndex, _queue.Count, _loopMode);
        if (nextIndex >= 0)
        {
            await StartTrackAsync(nextIndex);
            return;
        }

        _isPlaying = false;
        _isMediaLoaded = false;
        _discordPresence.Clear();
        UpdateUiState();
    }

    private PlaybackController EnsurePlaybackController()
    {
        if (_playbackController is not null)
        {
            return _playbackController;
        }

        _playbackController = new PlaybackController();
        _playbackController.PlaybackStarted += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _isPreparingTrack = false;
            _isMediaLoaded = true;
            _isPlaying = true;
            _positionTimer.Start();
            UpdateSeekUi();
            RecordTrackHistory();
            UpdateDiscordPresence();
            UpdateUiState();
        });
        _playbackController.PlaybackEnded += (_, _) => Dispatcher.UIThread.Post(async () => await ContinueAfterMediaEndedAsync());
        _playbackController.PlaybackFailed += message => Dispatcher.UIThread.Post(async () => await HandlePlaybackFailureAsync(new InvalidOperationException(message)));
        return _playbackController;
    }

    private void DisposeResources()
    {
        CancelTrackPreparation();
        try
        {
            _playbackController?.Stop(clearMedia: true);
        }
        catch
        {
        }
        _ffmpegAudioCache.Dispose();
        _playbackController?.Dispose();
        _discordPresence.Dispose();
    }

    private static IReadOnlyList<string> ResolveStoragePaths(IEnumerable<IStorageItem> storageItems)
        => storageItems
            .Select(static item => item.Path)
            .Where(static uri => uri.IsAbsoluteUri && uri.IsFile)
            .Select(static uri => Uri.UnescapeDataString(uri.LocalPath))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToList();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
