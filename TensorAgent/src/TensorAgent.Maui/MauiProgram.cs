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
using TensorAgent.Maui.Hosting;

namespace TensorAgent.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // The Web UI is TensorSharp.Server/wwwroot linked into the bundle as
        // webui/ (see the BundleResource item in the csproj).
        string webRoot = Path.Combine(NSBundle.MainBundle.BundlePath, "webui");
        builder.Services.AddSingleton(_ => new LoopbackWebHost(webRoot, EngineProbe.Run));
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<AppShell>();

        return builder.Build();
    }
}
