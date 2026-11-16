namespace QuickClip.Library;

/// <summary>A caller can stop waiting without releasing the underlying write's ownership.</summary>
internal static class SaveDeadline
{
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(8);

    internal static async Task<bool> WaitAsync(Task<bool> write, CancellationToken token)
    {
        try { return await write.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
    }
}
