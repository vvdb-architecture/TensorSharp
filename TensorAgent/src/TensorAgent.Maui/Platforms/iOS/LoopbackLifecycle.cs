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
/// Keeps the loopback server reachable across the app being suspended.
///
/// <para>
/// iOS reclaims the sockets of a suspended app — the listening socket included, and
/// 127.0.0.1 is no exception — about as soon as the app is suspended, which is thirty
/// seconds or so after it leaves the screen. The managed HttpListener the server is
/// built on cannot tell: its accept stays parked, nothing is thrown, nothing is
/// logged, and every connection the WebView opens is refused. To the page that is
/// "Load failed" on every request, and the only thing the user could do was force-quit
/// the app. That is the bug this class exists for.
/// </para>
/// <para>
/// The check runs on <c>WillEnterForeground</c>: before <c>DidBecomeActive</c>, and
/// before the WebView's content process is resumed, so the listener has been probed
/// — and rebuilt, if it was dead — by the time the page's own <c>visibilitychange</c>
/// handler asks the host anything. The page is nudged again afterwards regardless
/// (see <c>MainPage.OnForegroundChecked</c>), because its first attempt may have been
/// made against a listener that was still being rebuilt.
/// </para>
/// <para>
/// Off the main thread, always: UIKit delivers the notification on the main thread,
/// the probe is a network round trip, and a rebuild is a bind. Neither belongs there.
/// </para>
/// </summary>
internal sealed class LoopbackLifecycle : IDisposable
{
    private readonly AgentAppHost _host;
    private readonly List<NSObject> _observers = new();
    private int _checking;
    private bool _disposed;

    public LoopbackLifecycle(AgentAppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.WillEnterForegroundNotification, _ => Check()));
    }

    /// <summary>Probe the listener and repair it if it is dead. Coalesced: one check at a time.</summary>
    public void Check()
    {
        if (_disposed || Interlocked.Exchange(ref _checking, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _host.OnForegroundAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _host.TraceBackground("foreground: the listener check threw: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
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
