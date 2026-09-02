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
/// The two things the app has to ask the device about, both of which exist because
/// a setting would otherwise be a switch that does nothing.
/// </summary>
internal static class DeviceState
{
    /// <summary>
    /// Whether the only route to the internet right now is the cellular radio.
    ///
    /// <para>
    /// A catalog entry is five to ten gigabytes. Starting one of those on a metered
    /// connection because nobody checked is a real cost to a real person, so the
    /// "download over cellular" setting is enforced here rather than merely stored.
    /// MAUI's own connectivity profile is enough for this: it reports the active
    /// profiles, and cellular-only means cellular is present and WiFi and Ethernet
    /// are not.
    /// </para>
    /// </summary>
    public static bool IsOnCellularOnly()
    {
        try
        {
            IEnumerable<ConnectionProfile> profiles = Connectivity.Current.ConnectionProfiles;
            bool cellular = profiles.Contains(ConnectionProfile.Cellular);
            bool unmetered = profiles.Contains(ConnectionProfile.WiFi) || profiles.Contains(ConnectionProfile.Ethernet);
            return cellular && !unmetered;
        }
        catch (Exception)
        {
            // If the answer cannot be determined, do not block the download: a user
            // who asked for a model and got silence would have no way to find out why.
            return false;
        }
    }

    /// <summary>
    /// Keep the screen on, or stop keeping it on.
    ///
    /// <para>
    /// Generation on a phone is slow enough that the display sleeps partway through,
    /// and on iOS that suspends the app and stops the work. The alternative to this
    /// is a user watching a half-finished answer stop moving whenever they look away.
    /// It is only ever held while a reply is actually being generated.
    /// </para>
    /// </summary>
    public static void KeepAwake(bool on)
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(() => UIApplication.SharedApplication.IdleTimerDisabled = on);
        }
        catch (Exception)
        {
            // Not worth failing a generation over.
        }
    }
}
