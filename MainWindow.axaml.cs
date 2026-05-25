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
    private static readonly HashSet<string> SupportedExtensions = new(MediaFileTypes.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
    private const int MaxHistoryItems = 20;

    private readonly DispatcherTimer _positionTimer;
    private readonly AppSetupCoordinator _appSetupCoordinator;
    private readonly DiscordPresenceService _discordPresence;
    private readonly PlaybackHistoryStore _historyStore;
    private readonly ObservableCollection<AlbumHistoryEntry> _albumHistory = [];
    private readonly ObservableCollection<PlaybackItem> _queue = [];
    private readonly ObservableCollection<TrackHistoryEntry> _trackHistory = [];
    private readonly Random _random = new();
    private readonly TextBlock _trackTitleText;
    private readonly TextBlock _statusText;
    private readonly TextBlock _queueInfoText;
    private readonly TextBlock _sourceBadgeText;
    private readonly TextBlock _currentIndexText;
    private readonly TextBlock _queueCountText;
    private readonly TextBlock _loopModeBadgeText;
    private readonly TextBox _sourceInputTextBox;
    private readonly Button _addSourceButton;
    private readonly ProgressBar _preparationBar;
    private readonly Slider _seekSlider;
    private readonly TextBlock _elapsedText;
    private readonly TextBlock _remainingText;
    private readonly Button _openButton;
    private readonly Button _addAlbumButton;
    private readonly Button _prevButton;
    private readonly Button _playPauseButton;
    private readonly Button _nextButton;
    private readonly Button _loopButton;
    private readonly TextBlock _queueSummaryText;
    private readonly ListBox _queueList;
    private readonly ListBox _albumHistoryList;
    private readonly ListBox _trackHistoryList;
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

        _trackTitleText = this.FindControl<TextBlock>("TrackTitleText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _queueInfoText = this.FindControl<TextBlock>("QueueInfoText")!;
        _sourceBadgeText = this.FindControl<TextBlock>("SourceBadgeText")!;
        _currentIndexText = this.FindControl<TextBlock>("CurrentIndexText")!;
        _queueCountText = this.FindControl<TextBlock>("QueueCountText")!;
        _loopModeBadgeText = this.FindControl<TextBlock>("LoopModeBadgeText")!;
        _sourceInputTextBox = this.FindControl<TextBox>("SourceInputTextBox")!;
        _addSourceButton = this.FindControl<Button>("AddSourceButton")!;
        _preparationBar = this.FindControl<ProgressBar>("PreparationBar")!;
        _seekSlider = this.FindControl<Slider>("SeekSlider")!;
        _elapsedText = this.FindControl<TextBlock>("ElapsedText")!;
        _remainingText = this.FindControl<TextBlock>("RemainingText")!;
        _openButton = this.FindControl<Button>("OpenButton")!;
        _addAlbumButton = this.FindControl<Button>("AddAlbumButton")!;
        _prevButton = this.FindControl<Button>("PrevButton")!;
        _playPauseButton = this.FindControl<Button>("PlayPauseButton")!;
        _nextButton = this.FindControl<Button>("NextButton")!;
        _loopButton = this.FindControl<Button>("LoopButton")!;
        _queueSummaryText = this.FindControl<TextBlock>("QueueSummaryText")!;
        _queueList = this.FindControl<ListBox>("QueueList")!;
        _albumHistoryList = this.FindControl<ListBox>("AlbumHistoryList")!;
        _trackHistoryList = this.FindControl<ListBox>("TrackHistoryList")!;

        _albumHistoryList.ItemsSource = _albumHistory;
        _queueList.ItemsSource = _queue;
        _trackHistoryList.ItemsSource = _trackHistory;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _positionTimer.Tick += (_, _) => UpdateSeekUi();

        _openButton.Click += OpenButton_Click;
        _addAlbumButton.Click += AddAlbumButton_Click;
        _addSourceButton.Click += AddSourceButton_Click;
        _prevButton.Click += PrevButton_Click;
        _playPauseButton.Click += PlayPauseButton_Click;
        _nextButton.Click += NextButton_Click;
        _loopButton.Click += LoopButton_Click;
        _sourceInputTextBox.KeyDown += SourceInputTextBox_KeyDown;
        _seekSlider.PropertyChanged += SeekSlider_PropertyChanged;
        _seekSlider.AddHandler(InputElement.PointerPressedEvent, SeekSlider_PointerPressed, RoutingStrategies.Tunnel);
        _seekSlider.AddHandler(InputElement.PointerReleasedEvent, SeekSlider_PointerReleased, RoutingStrategies.Tunnel);
        _queueList.DoubleTapped += QueueList_DoubleTapped;
        _albumHistoryList.DoubleTapped += AlbumHistoryList_DoubleTapped;
        _trackHistoryList.DoubleTapped += TrackHistoryList_DoubleTapped;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        Closing += (_, _) => DisposeResources();

        _appSetupCoordinator = new AppSetupCoordinator();
        var options = CliOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _discordPresence = new DiscordPresenceService();
        _ffmpegAudioCache = new FfmpegAudioCache();
        _historyStore = new PlaybackHistoryStore();
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

    private async void OpenButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Select audio or video files",
            FileTypeFilter =
            [
                new FilePickerFileType("Media files")
                {
                    Patterns = [.. MediaFileTypes.SupportedExtensions.Select(static extension => $"*{extension}")]
                }
            ]
        });

        var added = AddSources(ResolveStoragePaths(files), append: false, shuffle: false);
        if (added.Count > 0)
        {
            await StartTrackAsync(0);
        }
    }

    private async void AddAlbumButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

    private async void AddSourceButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await AddSourceFromInputAsync();

    private async void PrevButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await GoToPreviousTrackAsync();

    private async void PlayPauseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await TogglePlaybackAsync();

    private async void NextButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await GoToNextTrackAsync();

    private void LoopButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _loopMode = _loopMode switch
        {
            LoopMode.None => LoopMode.All,
            LoopMode.All => LoopMode.One,
            _ => LoopMode.None
        };

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

        var target = TimeSpan.FromSeconds(_seekSlider.Value);
        _elapsedText.Text = FormatTime(target);
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
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void AlbumHistoryList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_albumHistoryList.SelectedItem is AlbumHistoryEntry entry)
        {
            await ReplayAlbumHistoryAsync(entry);
        }
    }

    private async void TrackHistoryList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_trackHistoryList.SelectedItem is TrackHistoryEntry entry)
        {
            await ReplayTrackHistoryAsync(entry);
        }
    }

    private async void QueueList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_queueList.SelectedItem is not PlaybackItem item)
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
        var droppedItems = e.Data.GetFiles();
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

        var addedItems = new List<PlaybackItem>();

        foreach (var source in rawSources.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            if (Directory.Exists(source))
            {
                var albumFiles = Directory.EnumerateFiles(source)
                    .Where(IsSupportedMediaFile)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var file in albumFiles)
                {
                    addedItems.Add(new PlaybackItem(file, true));
                }

                if (albumFiles.Count > 0)
                {
                    RecordAlbumHistory(source, albumFiles.Count);
                }

                continue;
            }

            if (_ytDlpAudioCache.IsSupportedUrl(source))
            {
                addedItems.Add(new PlaybackItem(source, false, source));
                continue;
            }

            if (File.Exists(source) && IsSupportedMediaFile(source))
            {
                addedItems.Add(new PlaybackItem(source, false));
            }
        }

        if (shuffle && addedItems.Count > 1)
        {
            ShuffleItems(addedItems);
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

        UpdateUiState();
        return addedItems;
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

        _trackTitleText.Text = item.DisplayName;
        _queueInfoText.Text = BuildQueueText(item);
        _queueList.SelectedIndex = index;
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

        var previousIndex = _currentIndex <= 0 ? _queue.Count - 1 : _currentIndex - 1;
        if (_currentIndex == 0 && _loopMode == LoopMode.None)
        {
            previousIndex = 0;
        }

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

        if (_currentIndex >= _queue.Count - 1)
        {
            if (_loopMode == LoopMode.None)
            {
                return;
            }

            await StartTrackAsync(0);
            return;
        }

        await StartTrackAsync(_currentIndex + 1);
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

        _playbackController.Position = TimeSpan.FromSeconds(_seekSlider.Value);
        UpdateSeekUi();
    }

    private void UpdateSeekUi()
    {
        if (!_isMediaLoaded || _playbackController is null || _playbackController.Duration <= TimeSpan.Zero)
        {
            _elapsedText.Text = "00:00";
            _remainingText.Text = "00:00";
            return;
        }

        var duration = _playbackController.Duration;
        var position = _playbackController.Position;

        if (!_isDraggingSeekBar)
        {
            _seekSlider.Maximum = Math.Max(duration.TotalSeconds, 1);
            _seekSlider.Value = Math.Clamp(position.TotalSeconds, 0, _seekSlider.Maximum);
        }

        _elapsedText.Text = FormatTime(position);
        _remainingText.Text = $"-{FormatTime(duration - position)}";
    }

    private void UpdateUiState()
    {
        _playPauseButton.Content = _isPreparingTrack ? "Loading" : _isPlaying ? "Pause" : "Play";
        _playPauseButton.IsEnabled = _queue.Count > 0 && !_isPreparingTrack && !_isRunningAppSetup;
        _prevButton.IsEnabled = _queue.Count > 0 && !_isRunningAppSetup;
        _nextButton.IsEnabled = _queue.Count > 0 && !_isRunningAppSetup;
        _openButton.IsEnabled = !_isRunningAppSetup;
        _addAlbumButton.IsEnabled = !_isRunningAppSetup;
        _addSourceButton.IsEnabled = !_isRunningAppSetup;
        _loopButton.IsEnabled = !_isRunningAppSetup;
        _sourceInputTextBox.IsEnabled = !_isRunningAppSetup;
        _seekSlider.IsEnabled = _isMediaLoaded && !_isPreparingTrack && !_isRunningAppSetup;
        _preparationBar.IsVisible = _isPreparingTrack || _isRunningAppSetup;

        _queueSummaryText.Text = _queue.Count == 0 ? "0 queued" : $"{_queue.Count} queued";
        _queueCountText.Text = _queue.Count == 0 ? "Queue empty" : $"{_queue.Count} queued";
        _currentIndexText.Text = _currentIndex >= 0 && _currentIndex < _queue.Count
            ? $"Track {_currentIndex + 1}/{_queue.Count}"
            : "Track --";
        _sourceBadgeText.Text = _currentIndex >= 0 && _currentIndex < _queue.Count
            ? _queue[_currentIndex].SourceLabel
            : "Standby";

        if (_queue.Count == 0)
        {
            _queueList.SelectedIndex = -1;
            _trackTitleText.Text = "Drop files or use Open.";
            _queueInfoText.Text = "Load a folder, pick files, or paste a URL to start playback.";
            if (!_isRunningAppSetup)
            {
                SetStatus("Ready for local files, albums, and URL playback.");
            }
            return;
        }

        if (_currentIndex >= 0 && _currentIndex < _queue.Count)
        {
            _queueList.SelectedIndex = _currentIndex;
            _queueInfoText.Text = BuildQueueText(_queue[_currentIndex]);
        }

        if (_isPreparingTrack || _isRunningAppSetup)
        {
            return;
        }

        if (_isPlaying)
        {
            SetStatus("Playing.");
        }
        else if (_isMediaLoaded)
        {
            SetStatus("Paused.");
        }
        else
        {
            SetStatus("Queue ready.");
        }
    }

    private void UpdateLoopButton()
    {
        var loopText = _loopMode switch
        {
            LoopMode.None => "Loop Off",
            LoopMode.One => "Loop One",
            _ => "Loop All"
        };

        _loopButton.Content = loopText.Replace("Loop ", "Loop: ");
        _loopModeBadgeText.Text = loopText;
    }

    private string BuildQueueText(PlaybackItem item)
        => $"{item.SourceLabel} {_currentIndex + 1}/{_queue.Count}  {item.ContextText}";

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
        => SupportedExtensions.Contains(Path.GetExtension(path));

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
            var extension = Path.GetExtension(item.Path);
            if (_ytDlpAudioCache.IsSupportedUrl(item.Path) && !_ytDlpAudioCache.IsAvailable)
            {
                message = "yt-dlp was not found in PATH or the bundled tools directory, so this URL could not be fetched.";
            }
            else if ((extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".opus", StringComparison.OrdinalIgnoreCase)) &&
                     !_ffmpegAudioCache.IsAvailable)
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
        var source = NormalizeSourceInput(_sourceInputTextBox.Text);
        if (!TryResolveInputSource(source, out var resolvedSource, out var validationMessage))
        {
            await DialogService.ShowInfoAsync(this, "Invalid source", validationMessage);
            return;
        }

        var added = AddSources([resolvedSource], append: _queue.Count > 0, shuffle: false);
        _sourceInputTextBox.Text = string.Empty;

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
            _trackTitleText.Text = item.DisplayName;
            _queueInfoText.Text = cachedAudio.WasCached
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
            SetStatus(item.IsAlbumSource ? "Cueing album track..." : "Cueing track...");
        }

        return await _ffmpegAudioCache.GetPlaybackPathAsync(item.Path, cancellationToken);
    }

    private void LoadHistory()
    {
        var history = _historyStore.Load();

        ReplaceHistoryItems(_albumHistory, history.Albums
            .OrderByDescending(static entry => entry.LastPlayedAt)
            .Take(MaxHistoryItems));
        ReplaceHistoryItems(_trackHistory, history.Tracks
            .OrderByDescending(static entry => entry.LastPlayedAt)
            .Take(MaxHistoryItems));
    }

    private void SaveHistory()
    {
        _historyStore.Save(new PlaybackHistorySnapshot
        {
            Albums = [.. _albumHistory],
            Tracks = [.. _trackHistory]
        });
    }

    private void RecordAlbumHistory(string albumPath, int trackCount)
    {
        if (string.IsNullOrWhiteSpace(albumPath) || trackCount <= 0)
        {
            return;
        }

        var displayName = Path.GetFileName(albumPath);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = albumPath;
        }

        var entry = new AlbumHistoryEntry(
            albumPath,
            displayName,
            trackCount,
            DateTimeOffset.Now);
        UpsertHistoryItem(
            _albumHistory,
            entry,
            static (left, right) => string.Equals(left.AlbumPath, right.AlbumPath, StringComparison.OrdinalIgnoreCase));
        SaveHistory();
    }

    private void RecordTrackHistory()
    {
        if (_currentIndex < 0 || _currentIndex >= _queue.Count)
        {
            return;
        }

        var item = _queue[_currentIndex];
        var entry = new TrackHistoryEntry(
            item.Path,
            item.DisplayName,
            BuildTrackHistoryContext(item),
            DateTimeOffset.Now);
        UpsertHistoryItem(
            _trackHistory,
            entry,
            static (left, right) => string.Equals(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase));
        SaveHistory();
    }

    private string BuildTrackHistoryContext(PlaybackItem item)
        => item.IsUrlSource ? item.Path : item.ContextText;

    private async Task ReplayAlbumHistoryAsync(AlbumHistoryEntry entry)
    {
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

    private static void ReplaceHistoryItems<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private static void UpsertHistoryItem<T>(ObservableCollection<T> collection, T item, Func<T, T, bool> isMatch)
    {
        var existingIndex = collection
            .Select((entry, index) => new { entry, index })
            .FirstOrDefault(candidate => isMatch(candidate.entry, item))
            ?.index ?? -1;

        if (existingIndex >= 0)
        {
            collection.RemoveAt(existingIndex);
        }

        collection.Insert(0, item);
        while (collection.Count > MaxHistoryItems)
        {
            collection.RemoveAt(collection.Count - 1);
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
        _seekSlider.Value = 0;
        _seekSlider.Maximum = 1;
        _elapsedText.Text = "00:00";
        _remainingText.Text = "00:00";
    }

    private void SetStatus(string message)
    {
        _statusText.Text = message;
    }

    private string BuildPreparationStatus(PlaybackItem item)
        => item.IsUrlSource
            ? "Preparing URL source..."
            : item.IsAlbumSource
                ? "Preparing album track..."
                : "Preparing track...";

    private void ShuffleItems(IList<PlaybackItem> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var swapIndex = _random.Next(i + 1);
            (items[i], items[swapIndex]) = (items[swapIndex], items[i]);
        }
    }

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

        if (_currentIndex < _queue.Count - 1)
        {
            await StartTrackAsync(_currentIndex + 1);
            return;
        }

        if (_loopMode == LoopMode.All && _queue.Count > 0)
        {
            await StartTrackAsync(0);
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
