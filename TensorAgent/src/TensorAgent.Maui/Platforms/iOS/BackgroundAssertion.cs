// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using UIKit;

namespace TensorAgent.Maui.Platforms.iOS;

/// <summary>
/// "Do not suspend us yet." One background-task assertion, held at most once, ended
/// exactly once.
///
/// <para>
/// The rules are unforgiving and are the whole reason this is a type rather than two
/// lines at each call site: an assertion taken twice loses the handle to the first, an
/// assertion never ended is a termination rather than a warning, and both must happen
/// on the main thread. Two things in this app need one — a model download and a
/// generation — and they needed the same twenty lines.
/// </para>
/// <para>
/// It buys "a little while", not an exemption: iOS decides how long, and the honest
/// design assumes the work will be cut short. What each user of this does about that is
/// its own business — a download resumes from its <c>.part</c>; a turn is written down
/// as far as it got.
/// </para>
/// </summary>
internal sealed class BackgroundAssertion : IDisposable
{
    private readonly string _name;
    private readonly object _gate = new();
    private nint _held = UIApplication.BackgroundTaskInvalid;
    private bool _disposed;

    public BackgroundAssertion(string name) => _name = name;

    /// <summary>Ask for more time, once.</summary>
    public void Acquire()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lock (_gate)
            {
                if (_disposed || _held != UIApplication.BackgroundTaskInvalid)
                    return;
                try
                {
                    // The expiration handler runs on the last of the borrowed time and
                    // does nothing but end the assertion, which is mandatory. The work
                    // is left to be cut short on its own terms.
                    _held = UIApplication.SharedApplication.BeginBackgroundTask(_name, Release);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"TensorAgent: could not hold {_name}: " + ex.Message);
                }
            }
        });
    }

    /// <summary>Give the time back. Safe to call when nothing is held.</summary>
    public void Release()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lock (_gate)
            {
                if (_held == UIApplication.BackgroundTaskInvalid)
                    return;
                nint held = _held;
                _held = UIApplication.BackgroundTaskInvalid;
                try { UIApplication.SharedApplication.EndBackgroundTask(held); }
                catch (Exception ex) { Console.WriteLine($"TensorAgent: ending {_name} failed: " + ex.Message); }
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        Release();
    }
}
