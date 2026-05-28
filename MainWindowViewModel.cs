using System.Collections.ObjectModel;
using System.Windows.Input;

namespace SimpleMusicPlayer;

public sealed class MainWindowViewModel : ViewModelBase
{
    private string _trackTitle = "Drop files or use Open.";
    private string _status = "Ready.";
    private string _queueInfo = "Supports local files, album folders, and yt-dlp-backed URL playback.";
    private string _sourceBadge = "Standby";
    private string _currentIndexText = "Track --";
    private string _queueCountText = "Queue empty";
    private string _loopModeBadge = "Loop All";
    private string _elapsedText = "00:00";
    private string _remainingText = "00:00";
    private string _sourceInput = string.Empty;
    private string _playPauseContent = "Play";
    private string _loopButtonContent = "Loop: All";
    private string _queueSummary = "0 queued";
    private double _seekValue;
    private double _seekMaximum = 1;
    private PlaybackItem? _selectedQueueItem;
    private AlbumHistoryEntry? _selectedAlbumHistory;
    private TrackHistoryEntry? _selectedTrackHistory;
    private bool _isPlayPauseEnabled;
    private bool _isPreviousEnabled;
    private bool _isNextEnabled;
    private bool _isOpenEnabled = true;
    private bool _isAddAlbumEnabled = true;
    private bool _isReverseQueueEnabled;
    private bool _isAddSourceEnabled = true;
    private bool _isLoopEnabled = true;
    private bool _isSourceInputEnabled = true;
    private bool _isSeekEnabled;
    private bool _isPreparationVisible;

    public ObservableCollection<AlbumHistoryEntry> AlbumHistory { get; } = [];
    public ObservableCollection<PlaybackItem> Queue { get; } = [];
    public ObservableCollection<TrackHistoryEntry> TrackHistory { get; } = [];

    public ICommand? OpenCommand { get; set; }
    public ICommand? AddAlbumCommand { get; set; }
    public ICommand? ReverseQueueCommand { get; set; }
    public ICommand? PreviousCommand { get; set; }
    public ICommand? PlayPauseCommand { get; set; }
    public ICommand? NextCommand { get; set; }
    public ICommand? LoopCommand { get; set; }
    public ICommand? AddSourceCommand { get; set; }
    public ICommand? RemoveAlbumHistoryCommand { get; set; }
    public ICommand? RemoveTrackHistoryCommand { get; set; }

    public string TrackTitle
    {
        get => _trackTitle;
        set => SetProperty(ref _trackTitle, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string QueueInfo
    {
        get => _queueInfo;
        set => SetProperty(ref _queueInfo, value);
    }

    public string SourceBadge
    {
        get => _sourceBadge;
        set => SetProperty(ref _sourceBadge, value);
    }

    public string CurrentIndexText
    {
        get => _currentIndexText;
        set => SetProperty(ref _currentIndexText, value);
    }

    public string QueueCountText
    {
        get => _queueCountText;
        set => SetProperty(ref _queueCountText, value);
    }

    public string LoopModeBadge
    {
        get => _loopModeBadge;
        set => SetProperty(ref _loopModeBadge, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        set => SetProperty(ref _elapsedText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        set => SetProperty(ref _remainingText, value);
    }

    public string SourceInput
    {
        get => _sourceInput;
        set => SetProperty(ref _sourceInput, value);
    }

    public string PlayPauseContent
    {
        get => _playPauseContent;
        set => SetProperty(ref _playPauseContent, value);
    }

    public string LoopButtonContent
    {
        get => _loopButtonContent;
        set => SetProperty(ref _loopButtonContent, value);
    }

    public string QueueSummary
    {
        get => _queueSummary;
        set => SetProperty(ref _queueSummary, value);
    }

    public double SeekValue
    {
        get => _seekValue;
        set => SetProperty(ref _seekValue, value);
    }

    public double SeekMaximum
    {
        get => _seekMaximum;
        set => SetProperty(ref _seekMaximum, value);
    }

    public PlaybackItem? SelectedQueueItem
    {
        get => _selectedQueueItem;
        set => SetProperty(ref _selectedQueueItem, value);
    }

    public AlbumHistoryEntry? SelectedAlbumHistory
    {
        get => _selectedAlbumHistory;
        set => SetProperty(ref _selectedAlbumHistory, value);
    }

    public TrackHistoryEntry? SelectedTrackHistory
    {
        get => _selectedTrackHistory;
        set => SetProperty(ref _selectedTrackHistory, value);
    }

    public bool IsPlayPauseEnabled
    {
        get => _isPlayPauseEnabled;
        set => SetProperty(ref _isPlayPauseEnabled, value);
    }

    public bool IsPreviousEnabled
    {
        get => _isPreviousEnabled;
        set => SetProperty(ref _isPreviousEnabled, value);
    }

    public bool IsNextEnabled
    {
        get => _isNextEnabled;
        set => SetProperty(ref _isNextEnabled, value);
    }

    public bool IsOpenEnabled
    {
        get => _isOpenEnabled;
        set => SetProperty(ref _isOpenEnabled, value);
    }

    public bool IsAddAlbumEnabled
    {
        get => _isAddAlbumEnabled;
        set => SetProperty(ref _isAddAlbumEnabled, value);
    }

    public bool IsReverseQueueEnabled
    {
        get => _isReverseQueueEnabled;
        set => SetProperty(ref _isReverseQueueEnabled, value);
    }

    public bool IsAddSourceEnabled
    {
        get => _isAddSourceEnabled;
        set => SetProperty(ref _isAddSourceEnabled, value);
    }

    public bool IsLoopEnabled
    {
        get => _isLoopEnabled;
        set => SetProperty(ref _isLoopEnabled, value);
    }

    public bool IsSourceInputEnabled
    {
        get => _isSourceInputEnabled;
        set => SetProperty(ref _isSourceInputEnabled, value);
    }

    public bool IsSeekEnabled
    {
        get => _isSeekEnabled;
        set => SetProperty(ref _isSeekEnabled, value);
    }

    public bool IsPreparationVisible
    {
        get => _isPreparationVisible;
        set => SetProperty(ref _isPreparationVisible, value);
    }
}
