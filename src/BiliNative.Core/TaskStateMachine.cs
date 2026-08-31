namespace BiliNative.Core;

public static class DownloadTaskStateMachine
{
    private static readonly Dictionary<DownloadTaskState, DownloadTaskState[]> Allowed =
        new Dictionary<DownloadTaskState, DownloadTaskState[]>
        {
            [DownloadTaskState.Queued] = [DownloadTaskState.Resolving, DownloadTaskState.Paused, DownloadTaskState.Cancelled],
            [DownloadTaskState.Resolving] = [DownloadTaskState.DownloadingVideo, DownloadTaskState.DownloadingAudio, DownloadTaskState.DownloadingSegments, DownloadTaskState.Paused, DownloadTaskState.Failed, DownloadTaskState.Cancelled],
            [DownloadTaskState.DownloadingVideo] = [DownloadTaskState.DownloadingAudio, DownloadTaskState.Merging, DownloadTaskState.Paused, DownloadTaskState.Failed, DownloadTaskState.Cancelled],
            [DownloadTaskState.DownloadingAudio] = [DownloadTaskState.DownloadingVideo, DownloadTaskState.Merging, DownloadTaskState.Paused, DownloadTaskState.Failed, DownloadTaskState.Cancelled],
            [DownloadTaskState.DownloadingSegments] = [DownloadTaskState.Merging, DownloadTaskState.Paused, DownloadTaskState.Failed, DownloadTaskState.Cancelled],
            [DownloadTaskState.Paused] = [DownloadTaskState.Resolving, DownloadTaskState.Cancelled],
            [DownloadTaskState.Merging] = [DownloadTaskState.Finalizing, DownloadTaskState.Paused, DownloadTaskState.Failed, DownloadTaskState.Cancelled],
            [DownloadTaskState.Finalizing] = [DownloadTaskState.Completed, DownloadTaskState.Failed, DownloadTaskState.Cancelled],
            [DownloadTaskState.Completed] = [],
            [DownloadTaskState.Failed] = [DownloadTaskState.Resolving, DownloadTaskState.Cancelled],
            [DownloadTaskState.Cancelled] = []
        };

    public static bool CanTransition(DownloadTaskState from, DownloadTaskState to) =>
        from == to || Allowed.TryGetValue(from, out var states) && states.Contains(to);

    public static DownloadTaskState Transition(DownloadTaskState from, DownloadTaskState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Illegal download state transition: {from} -> {to}.");
        }

        return to;
    }
}
