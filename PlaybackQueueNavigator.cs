namespace SimpleMusicPlayer;

internal static class PlaybackQueueNavigator
{
    public static int GetPreviousIndex(int currentIndex, int queueCount, LoopMode loopMode)
    {
        if (queueCount <= 0)
        {
            return -1;
        }

        if (currentIndex <= 0)
        {
            return loopMode == LoopMode.None ? 0 : queueCount - 1;
        }

        return currentIndex - 1;
    }

    public static int GetNextIndex(int currentIndex, int queueCount, LoopMode loopMode)
    {
        if (queueCount <= 0)
        {
            return -1;
        }

        if (currentIndex < 0)
        {
            return 0;
        }

        if (currentIndex >= queueCount - 1)
        {
            return loopMode == LoopMode.None ? -1 : 0;
        }

        return currentIndex + 1;
    }
}
