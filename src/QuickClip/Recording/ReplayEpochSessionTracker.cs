namespace QuickClip.Recording;

/// <summary>
/// An isolated managed contract for replay history across separately authorized
/// game-capture graphs. Actual native packet retention is not activated by this class.
/// Only independently verified observations may authorize another epoch.
/// </summary>
internal sealed class ReplayEpochSessionTracker(long maximumReconnectGapUs)
{
    private ReplayEpochGameProof? _last;
    private Guid _logicalSession;
    private long _epochNumber;
    private bool _active;

    internal sealed record Beginning(
        Guid LogicalSession, long EpochNumber, bool Continued,
        bool DiscardPreviousHistory);

    internal Guid LogicalSession => _logicalSession;
    internal long EpochNumber => _epochNumber;
    internal bool Active => _active;

    /// <summary>Call after the native graph has started and its source is verified.</summary>
    internal Beginning? Begin(ReplayEpochGameProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (!ReplayEpochContinuity.IsValidProof(proof))
        {
            Discard();
            return null;
        }

        // A graph can't begin twice without a verified final observation.
        if (_active)
        {
            Discard();
            return null;
        }

        bool continued = false;
        bool discard = false;
        if (_last is { } last)
        {
            var decision = ReplayEpochContinuity.Decide(last, proof, maximumReconnectGapUs);
            if (decision == ReplayEpochContinuityDecision.RevokeAndDiscard)
            {
                // This request crosses a changed grant generation; do not reuse the
                // current graph as an implicit authorization to retain old packets.
                Discard();
                return null;
            }
            continued = decision == ReplayEpochContinuityDecision.Continue;
            discard = !continued;
        }

        if (!continued)
        {
            _logicalSession = Guid.NewGuid();
            _epochNumber = 0;
        }
        _last = proof;
        _active = true;
        _epochNumber++;
        return new(_logicalSession, _epochNumber, continued, discard);
    }

    /// <summary>
    /// Only an independently verified stop is eligible for retaining its encoded ring.
    /// Invalid/stale identities and revoked permissions discard all previous history.
    /// </summary>
    internal bool End(ReplayEpochGameProof? proof)
    {
        if (!_active || _last is not { } last || proof == null)
        {
            Discard();
            return false;
        }
        var decision = ReplayEpochContinuity.Decide(
            last, proof, long.MaxValue);
        _active = false;
        if (decision != ReplayEpochContinuityDecision.Continue)
        {
            Discard();
            return false;
        }
        _last = proof;
        return true;
    }

    internal void Discard()
    {
        _last = null;
        _logicalSession = Guid.Empty;
        _epochNumber = 0;
        _active = false;
    }
}
