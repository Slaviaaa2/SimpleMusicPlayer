using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SimpleMusicPlayer;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedExtensions = new(MediaFileTypes.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
    private const int MaxHistoryItems = 20;

    private readonly DispatcherTimer _positionTimer;
    private readonly DiscordPresenceService _discordPresence;
    private readonly FfmpegAudioCache _ffmpegAudioCache;
    private readonly PlaybackHistoryStore _historyStore;
    private readonly YtDlpAudioCache _ytDlpAudioCache;
    private readonly ObservableCollection<AlbumHistoryEntry> _albumHistory = [];
    private readonly ObservableCollection<PlaybackItem> _queue = [];
    private readonly ObservableCollection<TrackHistoryEntry> _trackHistory = [];
    private readonly Random _random = new();
    private CancellationTokenSource? _trackLoadCts;
    private bool _isDraggingSeekBar;
    private bool _isPlaying;
    private bool _isMediaLoaded;
    private bool _isPreparingTrack;
    private int _currentIndex = -1;
    private LoopMode _loopMode = LoopMode.All;

    public MainWindow()
    {
        InitializeComponent();

        AlbumHistoryList.ItemsSource = _albumHistory;
        QueueList.ItemsSource = _queue;
        TrackHistoryList.ItemsSource = _trackHistory;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _positionTimer.Tick += (_, _) => UpdateSeekUi();

        var options = CliOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _discordPresence = new DiscordPresenceService(DiscordPresenceService.ResolveApplicationId(options));
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
    }

    protected override async void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Space)
        {
            await TogglePlaybackAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control)
        {
            await GoToPreviousTrackAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
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

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Select audio or video files",
            Filter = MediaFileTypes.OpenFileDialogFilter
        };

        if (dialog.ShowDialog(this) == true)
        {
            var added = AddSources(dialog.FileNames, append: false, shuffle: false);
            if (added.Count > 0)
            {
                await StartTrackAsync(0);
            }
        }
    }

    private async void AddAlbumButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select an album folder to append to the queue",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            AutoUpgradeEnabled = true
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var added = AddSources([dialog.SelectedPath], append: true, shuffle: false);
        if (added.Count > 0 && _currentIndex < 0)
        {
            await StartTrackAsync(0);
        }
    }

    private async void AddSourceButton_Click(object sender, RoutedEventArgs e) => await AddSourceFromInputAsync();

    private async void PrevButton_Click(object sender, RoutedEventArgs e) => await GoToPreviousTrackAsync();

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e) => await TogglePlaybackAsync();

    private async void NextButton_Click(object sender, RoutedEventArgs e) => await GoToNextTrackAsync();

    private void LoopButton_Click(object sender, RoutedEventArgs e)
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

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _isPreparingTrack = false;
        _isMediaLoaded = true;
        _isPlaying = true;
        _positionTimer.Start();
        UpdateSeekUi();
        Player.Play();
        RecordTrackHistory();
        UpdateDiscordPresence();
        UpdateUiState();
    }

    private async void Player_MediaEnded(object sender, RoutedEventArgs e)
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

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        HandlePlaybackFailure(e.ErrorException);
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isDraggingSeekBar = true;

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSeekBar = false;
        ApplySeekFromSlider();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingSeekBar)
        {
            var target = TimeSpan.FromSeconds(SeekSlider.Value);
            ElapsedText.Text = FormatTime(target);
        }
    }

    private async void SourceInputTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        await AddSourceFromInputAsync();
        e.Handled = true;
    }

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void AlbumHistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AlbumHistoryList.SelectedItem is AlbumHistoryEntry entry)
        {
            await ReplayAlbumHistoryAsync(entry);
        }
    }

    private async void TrackHistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackHistoryList.SelectedItem is TrackHistoryEntry entry)
        {
            await ReplayTrackHistoryAsync(entry);
        }
    }

    private async void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is not PlaybackItem item)
        {
            return;
        }

        var index = _queue.IndexOf(item);
        if (index >= 0)
        {
            await StartTrackAsync(index);
        }
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] droppedPaths)
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

        try
        {
            Player.Stop();
            Player.Source = null;
        }
        catch
        {
        }

        TrackTitleText.Text = item.DisplayName;
        QueueInfoText.Text = BuildQueueText(item);
        QueueList.SelectedIndex = index;
        QueueList.ScrollIntoView(item);
        SetStatus(BuildPreparationStatus(item));
        UpdateUiState();

        try
        {
            var playbackPath = await ResolvePlaybackPathAsync(item, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Player.Source = new Uri(playbackPath, UriKind.Absolute);
            Player.Position = TimeSpan.Zero;
            Player.Play();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                HandlePlaybackFailure(ex);
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

        if (_isPlaying)
        {
            Player.Pause();
            _positionTimer.Stop();
            _isPlaying = false;
        }
        else
        {
            Player.Play();
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

        if (_isMediaLoaded && Player.Position > TimeSpan.FromSeconds(3))
        {
            Player.Position = TimeSpan.Zero;
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
        if (!_isMediaLoaded || !Player.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var duration = Player.NaturalDuration.TimeSpan;
        var nextPosition = Player.Position + offset;
        nextPosition = TimeSpan.FromSeconds(Math.Clamp(nextPosition.TotalSeconds, 0, duration.TotalSeconds));
        Player.Position = nextPosition;
        UpdateSeekUi();
    }

    private void ApplySeekFromSlider()
    {
        if (!_isMediaLoaded || !Player.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        Player.Position = TimeSpan.FromSeconds(SeekSlider.Value);
        UpdateSeekUi();
    }

    private void UpdateSeekUi()
    {
        if (!_isMediaLoaded || !Player.NaturalDuration.HasTimeSpan)
        {
            ElapsedText.Text = "00:00";
            RemainingText.Text = "00:00";
            return;
        }

        var duration = Player.NaturalDuration.TimeSpan;
        var position = Player.Position;

        if (!_isDraggingSeekBar)
        {
            SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 1);
            SeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, SeekSlider.Maximum);
        }

        ElapsedText.Text = FormatTime(position);
        RemainingText.Text = $"-{FormatTime(duration - position)}";
    }

    private void UpdateUiState()
    {
        PlayPauseButton.Content = _isPreparingTrack ? "Loading" : _isPlaying ? "Pause" : "Play";
        PlayPauseButton.IsEnabled = _queue.Count > 0 && !_isPreparingTrack;
        PrevButton.IsEnabled = _queue.Count > 0;
        NextButton.IsEnabled = _queue.Count > 0;
        SeekSlider.IsEnabled = _isMediaLoaded && !_isPreparingTrack;
        PreparationBar.Visibility = _isPreparingTrack ? Visibility.Visible : Visibility.Collapsed;

        QueueSummaryText.Text = _queue.Count == 0 ? "0 queued" : $"{_queue.Count} queued";
        QueueCountText.Text = _queue.Count == 0 ? "Queue empty" : $"{_queue.Count} queued";
        CurrentIndexText.Text = _currentIndex >= 0 && _currentIndex < _queue.Count
            ? $"Track {_currentIndex + 1}/{_queue.Count}"
            : "Track --";
        SourceBadgeText.Text = _currentIndex >= 0 && _currentIndex < _queue.Count
            ? _queue[_currentIndex].SourceLabel
            : "Standby";

        if (_queue.Count == 0)
        {
            QueueList.SelectedIndex = -1;
            TrackTitleText.Text = "Drop files or use Open.";
            QueueInfoText.Text = "Load a folder, pick files, or paste a URL to start playback.";
            SetStatus("Ready for local files, albums, and URL playback.");
            return;
        }

        if (_currentIndex >= 0 && _currentIndex < _queue.Count)
        {
            QueueList.SelectedIndex = _currentIndex;
            QueueInfoText.Text = BuildQueueText(_queue[_currentIndex]);
        }

        if (_isPreparingTrack)
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

    protected override void OnClosed(EventArgs e)
    {
        CancelTrackPreparation();
        StopPlayback(clearSource: true);
        _ffmpegAudioCache.Dispose();
        _discordPresence.Dispose();
        base.OnClosed(e);
    }

    private void UpdateLoopButton()
    {
        var loopText = _loopMode switch
        {
            LoopMode.None => "Loop Off",
            LoopMode.One => "Loop One",
            _ => "Loop All"
        };

        LoopButton.Content = loopText.Replace("Loop ", "Loop: ");
        LoopModeBadgeText.Text = loopText;
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

    private void HandlePlaybackFailure(Exception? exception)
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
                message = "yt-dlp.exe was not found in PATH, so this URL could not be fetched.";
            }
            else if ((extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".opus", StringComparison.OrdinalIgnoreCase)) &&
                     !_ffmpegAudioCache.IsAvailable)
            {
                message = "ffmpeg.exe was not found in PATH, so this file could not be decoded.";
            }
        }

        System.Windows.MessageBox.Show(this, $"Could not play media.\n{message}", "Playback error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var source = NormalizeSourceInput(SourceInputTextBox.Text);
        if (!TryResolveInputSource(source, out var resolvedSource, out var validationMessage))
        {
            System.Windows.MessageBox.Show(this, validationMessage, "Invalid source", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var added = AddSources([resolvedSource], append: _queue.Count > 0, shuffle: false);
        SourceInputTextBox.Clear();

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

    private async Task<string> ResolvePlaybackPathAsync(PlaybackItem item, CancellationToken cancellationToken)
    {
        if (_ytDlpAudioCache.IsSupportedUrl(item.Path))
        {
            SetStatus("Fetching audio from URL...");
            var cachedAudio = await _ytDlpAudioCache.GetOrDownloadAsync(item.Path, cancellationToken);
            if (_ffmpegAudioCache.RequiresTranscode(cachedAudio.FilePath) && !_ffmpegAudioCache.IsAvailable)
            {
                throw new InvalidOperationException("ffmpeg.exe was not found in PATH, so this downloaded audio could not be decoded.");
            }

            item.UpdateDisplayName(cachedAudio.Title);
            TrackTitleText.Text = item.DisplayName;
            QueueInfoText.Text = cachedAudio.WasCached
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
                throw new InvalidOperationException("ffmpeg.exe was not found in PATH, so this file could not be decoded.");
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
            System.Windows.MessageBox.Show(this, "Album folder was not found.", "History", MessageBoxButton.OK, MessageBoxImage.Information);
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
            System.Windows.MessageBox.Show(this, "Track file was not found.", "History", MessageBoxButton.OK, MessageBoxImage.Information);
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
            Player.Stop();
            if (clearSource)
            {
                Player.Source = null;
            }
        }
        catch
        {
        }

        ResetTransportUi();
        _discordPresence.Clear();
    }

    private void ResetTransportUi()
    {
        SeekSlider.Value = 0;
        SeekSlider.Maximum = 1;
        ElapsedText.Text = "00:00";
        RemainingText.Text = "00:00";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
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
}
