using System;
using System.Collections.Generic;

namespace Bazaar.Server.GitHub.Webhooks;

/// <summary>
/// Remembers recent X-GitHub-Delivery ids so a redelivered webhook is applied once.
/// The window is count-based: once full, recording a new id evicts the oldest one.
/// Purely in memory; a restart forgets the window, and the database dedupe key covers that gap.
/// </summary>
public sealed class DeliveryDedupe
{
    // doc 21 names the dedupe key but does not fix the window size, so the caller chooses it.
    // The default is an arbitrary placeholder for tests and small installs, not a decided value.
    public const int DefaultCapacity = 10_000;

    private readonly int _capacity;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    /// <summary>Creates a dedupe window that holds at most <paramref name="capacity"/> delivery ids.</summary>
    /// <param name="capacity">Maximum remembered ids. Must be at least 1.</param>
    public DeliveryDedupe(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The window must hold at least one id.");
        }

        _capacity = capacity;
    }

    /// <summary>Number of delivery ids currently remembered.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _seen.Count;
            }
        }
    }

    /// <summary>
    /// Records the delivery id. Returns true when it is new and should be applied,
    /// or false when it is a duplicate already inside the window.
    /// </summary>
    /// <param name="deliveryId">The X-GitHub-Delivery header value. Must not be empty.</param>
    public bool TryRecord(string deliveryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        lock (_gate)
        {
            if (!_seen.Add(deliveryId))
            {
                return false;
            }

            _order.Enqueue(deliveryId);
            while (_order.Count > _capacity)
            {
                _seen.Remove(_order.Dequeue());
            }

            return true;
        }
    }

    /// <summary>Returns true when the id is currently inside the window, without recording it.</summary>
    /// <param name="deliveryId">The X-GitHub-Delivery header value.</param>
    public bool Contains(string deliveryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        lock (_gate)
        {
            return _seen.Contains(deliveryId);
        }
    }
}
