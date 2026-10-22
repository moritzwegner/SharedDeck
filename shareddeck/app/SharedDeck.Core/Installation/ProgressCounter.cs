namespace SharedDeck.Core.Installation;

/// <summary>Overall installation progress by bytes written, reported as 0..1 in steps of 0.1 %.</summary>
public sealed class ProgressCounter
{
    private readonly IProgress<double>? progress;
    private long total;
    private long done;
    private int lastPermille = -1;

    public ProgressCounter(IProgress<double>? progress) => this.progress = progress;

    public void AddTotal(long bytes) => total += bytes;

    public void Advance(long bytes)
    {
        done += bytes;
        Report();
    }

    public void Complete()
    {
        done = total;
        Report();
    }

    private void Report()
    {
        var fraction = total > 0 ? Math.Min(1.0, (double)done / total) : 1.0;
        var permille = (int)(fraction * 1000);
        if (permille != lastPermille)
        {
            lastPermille = permille;
            progress?.Report(fraction);
        }
    }
}
