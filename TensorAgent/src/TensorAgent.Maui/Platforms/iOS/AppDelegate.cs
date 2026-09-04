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
using TensorAgent.Maui.Platforms.iOS;
using UIKit;

namespace TensorAgent.Maui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// iOS asking for memory back is the last warning before jetsam, and the app used
    /// to ignore it entirely.
    ///
    /// <para>
    /// There is nothing safe to free from here -- the weights are a mapping the engine
    /// is reading, and the KV cache belongs to a generation that may be mid-token -- so
    /// this does not try. What it does is leave a record. A jetsam kill produces no
    /// stack, no exception and no message of its own; the app simply stops. Without
    /// this line the log of a kill ends at whatever the user happened to be doing, and
    /// the question "was it memory?" has no answer in it. With it, the warning and the
    /// remaining headroom are the last thing written.
    /// </para>
    /// </summary>
    [Export("applicationDidReceiveMemoryWarning:")]
    public void DidReceiveMemoryWarning(UIApplication application)
    {
        Console.WriteLine($"TensorAgent: iOS memory warning -- {DeviceState.DescribeMemory()}");
    }
}
