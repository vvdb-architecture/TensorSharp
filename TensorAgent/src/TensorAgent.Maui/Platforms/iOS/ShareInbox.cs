// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using Foundation;
using TensorAgent.Core.Hosting;
using UIKit;

namespace TensorAgent.Maui.Platforms.iOS;

/// <summary>
/// Makes sure a share the user made in another app actually arrives.
///
/// <para>
/// The durable App Group drop box is drained on every launch and return to the
/// foreground. A notification tap is merely a supported way to foreground the app;
/// delivery never depends on the notification.
/// </para>
/// </summary>
internal sealed class ShareInbox : IDisposable
{
    private readonly AgentAppHost _host;
    private readonly List<NSObject> _observers = new();
    private int _draining;
    private int _again;
    private int _started;
    private bool _disposed;

    public ShareInbox(AgentAppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        // Foreground rather than DidBecomeActive: WillEnterForeground runs before the
        // WebView's content process resumes, so the import is under way by the time the
        // page's own visibilitychange handler asks whether anything is waiting.
        _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.WillEnterForegroundNotification, _ => Drain()));

        // Startup draining is explicit (Start) so MainPage can subscribe to Arrived
        // first. Otherwise a fast text share can be imported before the only listener
        // that offers notification permission exists.
    }

    /// <summary>Begin delivery after the UI has subscribed to the intake events.</summary>
    public void Start()
    {
        if (_disposed || Interlocked.Exchange(ref _started, 1) == 1)
            return;
        Drain();
    }

    /// <summary>
    /// Import everything waiting in the App Group inbox.
    ///
    /// <para>
    /// Reentrancy-guarded rather than locked: a drain is I/O plus a file copy, and the
    /// startup and foreground callers can overlap. A second drain would find an empty
    /// box anyway; the guard only saves the work.
    /// </para>
    /// </summary>
    public void Drain()
    {
        if (_disposed || Volatile.Read(ref _started) == 0)
            return;
        // A drain is already running. Say so, so it goes round again for an envelope
        // committed while the first pass was enumerating.
        if (Interlocked.Exchange(ref _draining, 1) == 1)
        {
            Interlocked.Exchange(ref _again, 1);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _again, 0);
                    int imported = await _host.DrainSharedInboxAsync().ConfigureAwait(false);
                    if (imported > 0)
                        Console.WriteLine($"TensorAgent: share inbox imported {imported}");
                }
                while (Interlocked.CompareExchange(ref _again, 0, 1) == 1);
            }
            catch (Exception ex)
            {
                Console.WriteLine("TensorAgent: draining the share inbox failed: " + ex.Message);
            }
            finally
            {
                // Release, THEN re-check. A Drain() landing between the loop's exit test
                // and this line would otherwise see _draining still set, record _again,
                // and be answered by nobody: the worker has already left the loop, and
                // the next drain clears _again before ever acting on it. With the flag
                // released first, the loser of that race is a drain that starts here.
                Interlocked.Exchange(ref _draining, 0);
                if (Interlocked.Exchange(ref _again, 0) == 1)
                    Drain();
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (NSObject observer in _observers)
            NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
        _observers.Clear();
    }
}
