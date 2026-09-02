// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Maui;

public sealed class App : Application
{
    private readonly AppShell _shell;

    public App(AppShell shell)
    {
        _shell = shell;
        // The Web UI ships a single dark palette; the native chrome follows it
        // so the status bar and safe areas do not flash white around it.
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(_shell);
}
