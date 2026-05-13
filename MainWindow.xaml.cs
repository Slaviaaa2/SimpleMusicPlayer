using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SimpleMusicPlayer;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedExtensions =
    [
        ".mp3", ".wav", ".aac", ".m4a", ".flac", ".wma", ".ogg",
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm"
    ];

    private readonly DispatcherTimer _positionTimer;
    private readonly ObservableCollection<PlaybackItem> _queue = [];
    private readonly Random _random = new();
    private bool _isDraggingSeekBar;
    private bool _isPlaying;
    private bool _isMediaLoaded;
    private int _currentIndex = -1;
    private LoopMode _loopMode = LoopMode.All;

    public MainWindow()
    {
        InitializeComponent();

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _positionTimer.Tick += (_, _) => UpdateSeekUi();

        var options = CliOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _loopMode = options.LoopMode;
        UpdateLoopButton();

        if (options.Sources.Count > 0)
        {
            var added = AddSources(options.Sources, append: false, options.Shuffle);
            if (added.Count > 0)
            {
                var startIndex = Math.Clamp(options.StartIndex ?? 0, 0, added.Count - 1);
                StartTrack(startIndex);
            }
        }

        UpdateUiState();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Space)
        {
            TogglePlayback();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control)
        {
            GoToPreviousTrack();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
        {
            GoToNextTrack();
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

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Select audio or video files",
            Filter = "Media files|*.mp3;*.wav;*.aac;*.m4a;*.flac;*.wma;*.ogg;*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            var added = AddSources(dialog.FileNames, append: false, shuffle: false);
            if (added.Count > 0)
            {
                StartTrack(0);
            }
        }
    }

    private void AddAlbumButton_Click(object sender, RoutedEventArgs e)
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
            StartTrack(0);
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => GoToPreviousTrack();

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void NextButton_Click(object sender, RoutedEventArgs e) => GoToNextTrack();

    private void LoopButton_Click(object sender, RoutedEventArgs e)
    {
        _loopMode = _loopMode switch
        {
            LoopMode.None => LoopMode.All,
            LoopMode.All => LoopMode.One,
            _ => LoopMode.None
        };

        UpdateLoopButton();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _isMediaLoaded = true;
        UpdateSeekUi();
        Player.Play();
        _isPlaying = true;
        _positionTimer.Start();
        UpdateUiState();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        _positionTimer.Stop();

        if (_loopMode == LoopMode.One)
        {
            StartTrack(_currentIndex);
            return;
        }

        if (_currentIndex < _queue.Count - 1)
        {
            StartTrack(_currentIndex + 1);
            return;
        }

        if (_loopMode == LoopMode.All && _queue.Count > 0)
        {
            StartTrack(0);
            return;
        }

        _isPlaying = false;
        UpdateUiState();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _positionTimer.Stop();
        _isMediaLoaded = false;
        _isPlaying = false;
        UpdateUiState();
        System.Windows.MessageBox.Show(this, $"Could not play media.\n{e.ErrorException?.Message ?? "Unknown playback error."}", "Playback error", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
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
            StartTrack(append ? _queue.Count - added.Count : 0);
        }
    }

    private List<PlaybackItem> AddSources(IEnumerable<string> rawSources, bool append, bool shuffle)
    {
        if (!append)
        {
            _queue.Clear();
        }

        var addedItems = new List<PlaybackItem>();

        foreach (var source in rawSources.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            if (Directory.Exists(source))
            {
                foreach (var file in Directory.EnumerateFiles(source)
                             .Where(IsSupportedMediaFile)
                             .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
                {
                    addedItems.Add(new PlaybackItem(file, true));
                }
                continue;
            }

            if (File.Exists(source) && IsSupportedMediaFile(source))
            {
                addedItems.Add(new PlaybackItem(source, false));
            }
        }

        if (shuffle && addedItems.Count > 1)
        {
            addedItems = addedItems.OrderBy(_ => _random.Next()).ToList();
        }

        foreach (var item in addedItems)
        {
            _queue.Add(item);
        }

        if (_queue.Count == 0)
        {
            _currentIndex = -1;
        }
        else if (!append)
        {
            _currentIndex = 0;
        }

        UpdateUiState();
        return addedItems;
    }

    private void StartTrack(int index)
    {
        if (index < 0 || index >= _queue.Count)
        {
            return;
        }

        _currentIndex = index;
        _isMediaLoaded = false;
        _isPlaying = false;
        _positionTimer.Stop();
        SeekSlider.Value = 0;
        SeekSlider.Maximum = 1;

        var item = _queue[index];
        TrackTitleText.Text = item.DisplayName;
        QueueInfoText.Text = BuildQueueText(item);
        Player.Source = new Uri(item.Path, UriKind.Absolute);
        Player.Position = TimeSpan.Zero;
        Player.Play();
        UpdateUiState();
    }

    private void TogglePlayback()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        if (_currentIndex < 0)
        {
            StartTrack(0);
            return;
        }

        if (!_isMediaLoaded)
        {
            StartTrack(_currentIndex);
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

        UpdateUiState();
    }

    private void GoToPreviousTrack()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        if (_isMediaLoaded && Player.Position > TimeSpan.FromSeconds(3))
        {
            Player.Position = TimeSpan.Zero;
            return;
        }

        var previousIndex = _currentIndex <= 0 ? _queue.Count - 1 : _currentIndex - 1;
        if (_currentIndex == 0 && _loopMode == LoopMode.None)
        {
            previousIndex = 0;
        }

        StartTrack(previousIndex);
    }

    private void GoToNextTrack()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        if (_currentIndex >= _queue.Count - 1)
        {
            if (_loopMode == LoopMode.None)
            {
                return;
            }

            StartTrack(0);
            return;
        }

        StartTrack(_currentIndex + 1);
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
        PlayPauseButton.Content = _isPlaying ? "Pause" : "Play";
        PrevButton.IsEnabled = _queue.Count > 0;
        NextButton.IsEnabled = _queue.Count > 0;
        PlayPauseButton.IsEnabled = _queue.Count > 0;

        if (_queue.Count == 0)
        {
            TrackTitleText.Text = "Drop files or use Open.";
            QueueInfoText.Text = "Example: SimpleMusicPlayer.exe --album D:\\Music\\Album --loop all --shuffle";
        }
    }

    private void UpdateLoopButton()
    {
        LoopButton.Content = _loopMode switch
        {
            LoopMode.None => "Loop: Off",
            LoopMode.One => "Loop: One",
            _ => "Loop: All"
        };
    }

    private string BuildQueueText(PlaybackItem item)
    {
        var prefix = item.IsAlbumSource ? "Album" : "Queue";
        return $"{prefix} {_currentIndex + 1}/{_queue.Count}  {item.Path}";
    }

    private static bool IsSupportedMediaFile(string path)
        => SupportedExtensions.Contains(System.IO.Path.GetExtension(path));

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
}
