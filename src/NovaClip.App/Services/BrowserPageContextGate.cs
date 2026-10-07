using NovaClip.Bilibili;

namespace NovaClip.App;

// DOM Source can still identify the outgoing document while a native navigation
// already owns a different target. Both identities must agree before observing it.
internal static class BrowserPageContextGate
{
    public static bool AcceptsContext(Uri observedPage, Uri currentSource, Uri? intendedPage) =>
        BilibiliMediaIdentity.IsSamePage(observedPage, currentSource) &&
        (intendedPage is null || BilibiliMediaIdentity.IsSamePage(observedPage, intendedPage));

    public static bool AcceptsSource(Uri currentSource, Uri? pendingTarget) =>
        pendingTarget is null || BilibiliMediaIdentity.IsSamePage(currentSource, pendingTarget);

    public static bool RequiresPageRestore(Uri currentSource, Uri? authoritativePage) =>
        authoritativePage is null || !BilibiliMediaIdentity.IsSamePage(currentSource, authoritativePage);
}
