namespace SimpleMusicPlayer;

public sealed record PlaybackItem(string Path, bool IsAlbumSource)
{
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path);
}
