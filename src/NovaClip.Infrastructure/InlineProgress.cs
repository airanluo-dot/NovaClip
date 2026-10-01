namespace NovaClip.Infrastructure;

// Internal pipeline progress is already synchronized by the task/track gates. Posting it
// through Progress<T> adds a second queue and allows callbacks to arrive after completion.
// UI subscribers marshal/coalesce independently at their own boundary.
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
