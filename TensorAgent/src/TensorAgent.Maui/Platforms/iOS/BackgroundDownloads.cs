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
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Downloads;
using UIKit;

namespace TensorAgent.Maui.Platforms.iOS;

/// <summary>
/// Keeps a model download alive across the two things that used to end it: leaving the
/// page, and leaving the app.
///
/// <para>
/// The first is <see cref="ModelDownloadManager"/>'s job and needs nothing from iOS.
/// This is the second half. When the user switches to another app, iOS suspends the
/// process at the next opportunity, and a suspended process is not reading a socket: the
/// transfer dies with an I/O error nobody asked for. A background-task assertion asks the
/// system to postpone that while bytes are still moving, so a glance at a message or a
/// look-up in another app costs the download nothing.
/// </para>
/// <para>
/// It is a postponement, not an exemption, and the honest description of what it buys is
/// "a little while" — iOS decides how long, and for a five-gigabyte model it will not be
/// long enough. That is why the other half matters more: every file is written through a
/// <c>.part</c>, so a transfer the system does eventually stop resumes from the byte it
/// reached, and <see cref="ModelDownloadManager.ResumeInterrupted"/> restarts it by
/// itself the moment the app is in front of the user again. The user never taps twice and
/// never loses a gigabyte.
/// </para>
/// </summary>
internal sealed class BackgroundDownloads : IDisposable
{
    private readonly ModelDownloadManager _downloads;
    private readonly List<NSObject> _observers = new();
    private readonly BackgroundAssertion _assertion = new("TensorAgent.ModelDownload");
    private bool _disposed;

    public BackgroundDownloads(ModelDownloadManager downloads)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));

        _downloads.BusyChanged += OnBusyChanged;
        _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.WillEnterForegroundNotification, _ => Resume()));
        // Asked for again on the way out as well as on the first byte: a download that
        // was already running when the user left never raised BusyChanged, and that is
        // precisely the case this exists for.
        _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.DidEnterBackgroundNotification, _ => { if (_downloads.IsBusy) _assertion.Acquire(); }));
    }

    private void OnBusyChanged(bool busy)
    {
        if (busy) _assertion.Acquire();
        else _assertion.Release();
    }

    /// <summary>
    /// Back in the foreground: restart anything the suspension killed, from where it
    /// stopped.
    /// </summary>
    private void Resume()
    {
        if (_disposed)
            return;
        try
        {
            IReadOnlyList<string> resumed = _downloads.ResumeInterrupted(ModelCatalog.Find);
            if (resumed.Count > 0)
                Console.WriteLine("TensorAgent: resumed " + string.Join(", ", resumed));
        }
        catch (Exception ex)
        {
            Console.WriteLine("TensorAgent: resuming downloads failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _downloads.BusyChanged -= OnBusyChanged;
        foreach (NSObject observer in _observers)
            NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
        _observers.Clear();
        _assertion.Dispose();
    }
}
