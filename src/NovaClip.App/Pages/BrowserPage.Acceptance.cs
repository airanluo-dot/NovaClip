using Microsoft.UI.Xaml;
using NovaClip.Contracts;
using NovaClip.Core;
namespace NovaClip.App.Pages;
// Acceptance-only instrumentation; production beta.8 recognition is unchanged.
public sealed partial class BrowserPage
{
    internal MediaDetectionSnapshot AcceptanceSnapshot => _detector.Snapshot;
    internal bool IsPageBridgeReady => _initializationTask?.IsCompletedSuccessfully == true && HasInitializedWebView;
    internal bool IsMediaCardReady => AddDownloadButton.IsEnabled && MediaDetails.Visibility == Visibility.Visible;
    internal Task<string> ExecuteAcceptanceScriptAsync(string script) => BrowserWebView.CoreWebView2?.ExecuteScriptAsync(script) ?? Task.FromResult("null");
    internal void SelectLowestAcceptanceQuality()
    {
        if (_detector.Snapshot.Media is not MediaDescriptor media) return;
        var tracks=media.Tracks.Where(track=>track.Type==TrackType.Video).ToList();
        if(tracks.Count>0) QualityCombo.SelectedIndex=tracks.IndexOf(tracks.OrderBy(track=>track.QualityId ?? 0).First());
    }
    internal async Task<Guid?> EnqueueCurrentMediaAsync()
    {
        var previous=AppServices.Downloads.GetTasks().Select(task=>task.Id).ToHashSet();
        AddDownloadButton_Click(this,new RoutedEventArgs());
        for(var attempt=0;attempt<20;attempt++)
        {
            if(AppServices.Downloads.GetTasks().FirstOrDefault(task=>!previous.Contains(task.Id)) is { } created) return created.Id;
            await Task.Delay(250);
        }
        return null;
    }
}
