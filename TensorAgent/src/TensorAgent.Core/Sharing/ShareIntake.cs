// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Core.Sharing;

/// <summary>
/// One share, imported and ready for the composer.
/// </summary>
/// <param name="Id">The share's id, for the log and for a duplicate check.</param>
/// <param name="Text">The message, exactly as it should appear in the box.</param>
/// <param name="Attachments">
/// The upload service's own answers, VERBATIM.
///
/// <para>
/// Verbatim is a hard requirement rather than tidiness. The page decides which of six
/// path lists a file belongs in from members of this object — <c>mediaType</c>,
/// <c>frames</c>, <c>textContent</c>, <c>fileBacked</c>, <c>previewUrl</c> — and a
/// hand-built <c>{file, fileName}</c> would be sent to the model as a bare text path.
/// It is the same object <c>MainPage.AttachAsync</c> forwards for a photo pick.
/// </para>
/// </param>
/// <param name="Notices">Sentences to show the user once, about what did not fit or did not come.</param>
/// <param name="Title">A name for the chat this share starts, better than its first line.</param>
/// <param name="NewChat">
/// Always true for imported envelopes. Retained in the page contract so an app and
/// WebView from adjacent builds remain compatible while every share starts fresh.
/// </param>
/// <param name="AutoSend">Compatibility field for old envelopes; current UI never auto-sends.</param>
public sealed record PendingShare(
    string Id,
    string Text,
    IReadOnlyList<object> Attachments,
    IReadOnlyList<string> Notices,
    string Title,
    bool NewChat,
    bool AutoSend);

public enum ShareOfferStatus
{
    Offered,
    Duplicate,
    Full,
}

/// <summary>
/// Where an imported share waits for the page to come and get it.
///
/// <para>
/// It waits HERE, on the host, and the page PULLS — rather than the app pushing the
/// share into the WebView the moment it arrives. That is not a style choice. A share
/// launch usually starts the app cold, so at the instant the URL arrives there is no
/// page to push to; and WebKit reclaims the content process of a WebView whose view
/// left the window, so even a warm push can land on a page that is about to be thrown
/// away and reloaded knowing nothing. Anything that must survive that belongs to the
/// host and is re-attached to — which is exactly the rule <c>ChatTurnManager</c> and
/// <c>ModelDownloadManager</c> already follow.
/// </para>
/// <para>
/// The page asks at every point where it could have missed one: at the end of its boot
/// chain, and on <c>visibilitychange</c>. The app additionally nudges it when a share
/// arrives while it is already running, so a share made from another app appears
/// immediately rather than at the next glance.
/// </para>
/// </summary>
public sealed class ShareIntake
{
    private readonly object _lock = new();
    private readonly Queue<PendingShare> _pending = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// How many shares may wait at once.
    ///
    /// <para>
    /// Small on purpose. These are things the user did seconds ago expecting an answer;
    /// a queue deep enough to matter means the app never came forward. Backpressure
    /// leaves later envelopes durable on disk until each earlier share has had its own
    /// chat, instead of staging an unbounded number of large attachments in memory.
    /// </para>
    /// </summary>
    public int MaxPending { get; init; } = 4;

    /// <summary>
    /// Raised when a share arrives or acknowledging the head reveals another one and
    /// the page should be nudged. Never on the UI thread.
    /// </summary>
    public event Action? Arrived;

    /// <summary>
    /// Invoked synchronously when an accepted chat request is about to consume the
    /// shared draft. Every listener must durably acknowledge it before this queue can
    /// remove it; false or an exception leaves the share pending.
    /// </summary>
    public event Func<string, bool>? Acknowledging;

    /// <summary>How many shares are waiting.</summary>
    public int PendingCount
    {
        get { lock (_lock) return _pending.Count; }
    }

    /// <summary>Whether another durable share can be imported without eviction.</summary>
    public bool CanAccept
    {
        get { lock (_lock) return _pending.Count < MaxPending; }
    }

    /// <summary>
    /// Add a share, refusing it if the queue is full, and ignoring one whose id has
    /// already been offered.
    ///
    /// <para>
    /// The id check is what makes delivery exactly-once within a launch. Startup,
    /// foreground and acknowledgement drains can overlap, and a share offered twice
    /// would otherwise be pre-filled twice or sent twice.
    /// </para>
    /// </summary>
    /// <returns>Whether it was added.</returns>
    public bool Offer(PendingShare share)
        => TryOffer(share) == ShareOfferStatus.Offered;

    /// <summary>Add a share and report why it was not added, without evicting data.</summary>
    public ShareOfferStatus TryOffer(PendingShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        lock (_lock)
        {
            if (share.Id.Length > 0 && !_seen.Add(share.Id))
                return ShareOfferStatus.Duplicate;
            // Never evict a share after its files have already been copied into the
            // app. The durable envelope remains claimable and a later drain retries it.
            if (_pending.Count >= MaxPending)
            {
                if (share.Id.Length > 0)
                    _seen.Remove(share.Id);
                return ShareOfferStatus.Full;
            }
            _pending.Enqueue(share);
        }
        SignalArrived();
        return ShareOfferStatus.Offered;
    }

    /// <summary>
    /// The next share, or null. Looking does not consume it: an accepted, durably
    /// recorded chat request (or explicit user discard) acknowledges it.
    /// </summary>
    public PendingShare? Peek()
    {
        lock (_lock)
            return _pending.Count == 0 ? null : _pending.Peek();
    }

    /// <summary>
    /// Consume the head share after an accepted send or explicit discard. An
    /// out-of-order or duplicate acknowledgement is harmless and returns false.
    /// </summary>
    public bool Acknowledge(string id)
    {
        bool nextWasRevealed;
        lock (_lock)
        {
            if (_pending.Count == 0
                || !string.Equals(_pending.Peek().Id, id, StringComparison.Ordinal))
            {
                return false;
            }

            Func<string, bool>? listeners = Acknowledging;
            if (listeners is not null)
            {
                foreach (Func<string, bool> listener in listeners.GetInvocationList())
                {
                    try
                    {
                        if (!listener(id))
                            return false;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }
            }

            _pending.Dequeue();
            nextWasRevealed = _pending.Count > 0;
        }

        // Offer() announced every item as it entered the queue, but several durable
        // envelopes are normally imported in one drain. While the first remains the
        // leased head, all of those early announcements can only rediscover that same
        // draft. Removing the head is therefore a new availability transition in its
        // own right: announce the item it reveals, or it stays invisible until some
        // unrelated foreground/pageshow event happens to ask again.
        if (nextWasRevealed)
            SignalArrived();
        return true;
    }

    private void SignalArrived()
    {
        Action? arrived = Arrived;
        if (arrived is null)
            return;

        // Notification is advisory; a broken UI listener must never turn a share that
        // is already queued into an importer failure or a failed acknowledgement.
        foreach (Action listener in arrived.GetInvocationList())
        {
            try { listener(); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Forget that a share id was ever seen, so it can be offered again.
    ///
    /// <para>
    /// For the one case where re-delivery is right: the page could not apply the share
    /// and said so. Without this the user's content would be gone for good, having been
    /// consumed by a page that then failed to use it.
    /// </para>
    /// </summary>
    public void Forget(string id)
    {
        lock (_lock)
            _seen.Remove(id);
    }
}
