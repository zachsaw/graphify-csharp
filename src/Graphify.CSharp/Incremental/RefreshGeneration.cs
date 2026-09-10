namespace Graphify.CSharp.Incremental;

internal sealed record RefreshTarget
{
    public RefreshTarget(Guid sessionId, long eventGeneration)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A refresh target must belong to a session.", nameof(sessionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(eventGeneration);
        SessionId = sessionId;
        EventGeneration = eventGeneration;
    }

    public Guid SessionId { get; }

    public long EventGeneration { get; }
}

internal sealed record RefreshGeneration
{
    public RefreshGeneration(
        Guid sessionId,
        long eventGeneration = 0,
        long indexedGeneration = 0,
        long publishedGeneration = 0)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A generation must belong to a session.", nameof(sessionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(eventGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(indexedGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(publishedGeneration);
        if (indexedGeneration > eventGeneration)
        {
            throw new ArgumentException("Indexed generation cannot exceed event generation.", nameof(indexedGeneration));
        }

        if (publishedGeneration > indexedGeneration)
        {
            throw new ArgumentException("Published generation cannot exceed indexed generation.", nameof(publishedGeneration));
        }

        SessionId = sessionId;
        EventGeneration = eventGeneration;
        IndexedGeneration = indexedGeneration;
        PublishedGeneration = publishedGeneration;
    }

    public Guid SessionId { get; }

    public long EventGeneration { get; }

    public long IndexedGeneration { get; }

    public long PublishedGeneration { get; }

    public RefreshTarget CaptureTarget() => new(SessionId, EventGeneration);

    public RefreshGeneration RecordEvent() => new(
        SessionId,
        checked(EventGeneration + 1),
        IndexedGeneration,
        PublishedGeneration);

    public RefreshGeneration AdvanceEventsThrough(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        if (generation < EventGeneration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                generation,
                "Event generation cannot move backwards.");
        }

        return generation == EventGeneration
            ? this
            : new RefreshGeneration(SessionId, generation, IndexedGeneration, PublishedGeneration);
    }

    public RefreshGeneration MarkIndexed(long generation)
    {
        ValidateAdvancement(generation, IndexedGeneration, EventGeneration, nameof(generation));
        return new RefreshGeneration(SessionId, EventGeneration, generation, PublishedGeneration);
    }

    public RefreshGeneration MarkPublished(long generation)
    {
        ValidateAdvancement(generation, PublishedGeneration, IndexedGeneration, nameof(generation));
        return new RefreshGeneration(SessionId, EventGeneration, IndexedGeneration, generation);
    }

    public bool IsIndexedThrough(RefreshTarget target) => IsThrough(target, IndexedGeneration);

    public bool IsPublishedThrough(RefreshTarget target) => IsThrough(target, PublishedGeneration);

    private bool IsThrough(RefreshTarget target, long generation)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.SessionId == SessionId && generation >= target.EventGeneration;
    }

    private static void ValidateAdvancement(long requested, long current, long upperBound, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requested, parameterName);
        if (requested < current || requested > upperBound)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                requested,
                $"Generation must advance monotonically between {current} and {upperBound}.");
        }
    }
}
