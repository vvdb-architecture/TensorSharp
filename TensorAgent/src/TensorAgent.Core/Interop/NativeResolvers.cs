// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Reflection;
using System.Runtime.InteropServices;

namespace TensorAgent.Core.Interop;

/// <summary>
/// The one <c>DllImport</c> resolver this assembly registers, and the list of
/// interops that share it.
///
/// <para>
/// .NET allows exactly ONE resolver per assembly, and two types here want one:
/// CPython, which maps <c>__Internal</c> to the app image or to a developer
/// machine's <c>libpython3.13</c>, and JavaScriptCore, which maps its framework.
/// They used to race for the single slot, and the loser was told to try harder
/// next time — the comment on the Python side literally read "build the Python
/// runtime before the JavaScript one".
/// </para>
/// <para>
/// That was not a documentation problem. Losing meant CPython's imports could not
/// bind AT ALL for the life of the process, so an app whose first tool call
/// happened to be JavaScript had no Python for the rest of the launch, and every
/// <c>python3</c> after it answered "the CPython entry points could not be bound"
/// — for a reason that had nothing to do with Python. One resolver that asks each
/// registered interop in turn removes the race rather than documenting it: order
/// no longer decides anything, because there is nothing left to lose.
/// </para>
/// </summary>
internal static class NativeResolvers
{
    private static readonly object Gate = new();
    private static readonly List<DllImportResolver> Handlers = new();
    private static bool s_registered;

    /// <summary>
    /// Non-null when even this shared resolver could not be registered — something
    /// outside this file claimed the slot. Reported rather than swallowed, because
    /// the symptom (imports that will not bind) names nothing that would find it.
    /// </summary>
    internal static string? Conflict { get; private set; }

    /// <summary>
    /// Add an interop's resolver. It is asked for every library name this assembly
    /// imports and must return <see cref="IntPtr.Zero"/> for the ones that are not
    /// its own, which is what lets several coexist.
    /// </summary>
    internal static void Register(DllImportResolver handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Gate)
        {
            if (!Handlers.Contains(handler))
                Handlers.Add(handler);
            if (s_registered)
                return;
            s_registered = true;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeResolvers).Assembly, Dispatch);
            }
            catch (InvalidOperationException ex)
            {
                Conflict = "a DllImport resolver for TensorAgent.Core was registered outside "
                    + $"NativeResolvers ({ex.Message.Trim()})";
            }
        }
    }

    private static IntPtr Dispatch(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        DllImportResolver[] handlers;
        lock (Gate)
            handlers = Handlers.ToArray();

        foreach (DllImportResolver handler in handlers)
        {
            IntPtr handle = handler(libraryName, assembly, searchPath);
            if (handle != IntPtr.Zero)
                return handle;
        }
        return IntPtr.Zero;
    }
}
