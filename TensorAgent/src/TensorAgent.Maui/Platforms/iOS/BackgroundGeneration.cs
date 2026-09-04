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
using TensorAgent.Core.Sessions;
using TensorAgent.Core.Settings;
using UIKit;

namespace TensorAgent.Maui.Platforms.iOS;

/// <summary>
/// Keeps a generation alive across the two things that used to end it: the display
/// going to sleep, and the user leaving the app.
///
/// <para>
/// The third — leaving the chat for another screen — is
/// <see cref="ChatTurnManager"/>'s job and needs nothing from iOS. This is the half
/// only the platform can answer. A phone dims and then locks after a minute or two of
/// no touches, and a locked phone suspends the app; a turn that takes ninety seconds is
/// therefore routinely killed by the user doing nothing at all. Switching to another
/// app suspends it immediately.
/// </para>
/// <para>
/// It follows the HOST's turns rather than the page's idea of them, which is the whole
/// reason it can be written at all: the page stops reading whenever it is navigated
/// away from, and asking it whether the model is working would answer "no" at exactly
/// the moments this exists for.
/// </para>
/// </summary>
internal sealed class BackgroundGeneration : IDisposable
{
    private readonly ChatTurnManager _turns;
    private readonly SettingsStore _settings;
    private readonly BackgroundAssertion _assertion = new("TensorAgent.Generation");
    private readonly List<NSObject> _observers = new();

    public BackgroundGeneration(ChatTurnManager turns, SettingsStore settings)
    {
        _turns = turns ?? throw new ArgumentNullException(nameof(turns));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _turns.BusyChanged += OnBusyChanged;
        // Asked for again on the way out as well as on the first token: a turn that was
        // already running when the user left never raises BusyChanged, and that is
        // precisely the case this exists for.
        _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.DidEnterBackgroundNotification, _ => { if (_turns.IsBusy) _assertion.Acquire(); }));
    }

    private void OnBusyChanged(bool busy)
    {
        // The display is held awake for exactly the stretch the model is working, and
        // only when the user has left that setting on. It is a real cost to their
        // battery, so it is never held a moment longer than the answer takes.
        DeviceState.KeepAwake(busy && _settings.Load().KeepAwakeWhileGenerating);
        if (busy)
            _assertion.Acquire();
        else
            _assertion.Release();
    }

    public void Dispose()
    {
        _turns.BusyChanged -= OnBusyChanged;
        foreach (NSObject observer in _observers)
            NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
        _observers.Clear();
        DeviceState.KeepAwake(false);
        _assertion.Dispose();
    }
}
