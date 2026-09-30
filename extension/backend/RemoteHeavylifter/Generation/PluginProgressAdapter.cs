namespace RemoteHeavylifter.Generation;

/// <summary>
/// Jobs started from Cove's task list receive the plain extension progress (percent + message only).
/// This lets them drive the same coordinator; per-video units collapse into the aggregate message.
/// </summary>
internal sealed class PluginProgressAdapter(Cove.Plugins.IJobProgress inner) : Cove.Core.Interfaces.IJobProgress
{
    public void Report(double progress, string? subTask = null) => inner.Report(progress, subTask);

    public void SetSummary(string summary) => inner.Report(1d, summary);
}
