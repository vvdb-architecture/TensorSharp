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

/// <summary>
/// Single-page shell for the spike. The sessions drawer, models, skills and
/// settings pages of the full app hang off this later.
/// </summary>
public sealed class AppShell : Shell
{
    public AppShell(MainPage mainPage)
    {
        Title = "TensorAgent";
        Items.Add(new ShellContent
        {
            Title = "Chat",
            Route = "main",
            Content = mainPage,
        });
    }
}
