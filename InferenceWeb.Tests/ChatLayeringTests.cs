// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System;
using System.Linq;
using System.Reflection;
using TensorSharp.Chat;
using TensorSharp.Server.Hosting;
using TensorSharp.Server.ProtocolAdapters;
using TensorSharp.Server.ResponseSerializers;
using TensorSharp.Server.Skills;
using TensorSharp.Server.StreamingWriters;
using Xunit;

namespace InferenceWeb.Tests;

/// <summary>
/// The layering the TensorSharp.Chat extraction exists to create: the chat pipeline —
/// model service, sessions, generation, the skills loop and the Web UI request/stream
/// contract — lives in a plain class library that TensorSharp.Server, TensorSharp.Cli
/// and the iOS app all build on, and that library carries no ASP.NET Core and no
/// TensorSharp.Distributed (which drags in the CUDA backend an iOS build cannot link).
///
/// <para>
/// Mirrors <see cref="AgentHostLayeringTests"/>: a compile-time reference would enforce
/// the direction on its own, except that adding one back is a one-line mistake nobody
/// notices until the app stops linking. These fail the moment it happens.
/// </para>
/// </summary>
public class ChatLayeringTests
{
    private static Assembly Chat => typeof(WebUiChatService).Assembly;
    private static Assembly Server => typeof(WebUiAdapter).Assembly;

    [Fact]
    public void TheChatLibrary_IsItsOwnAssembly()
    {
        Assert.Equal("TensorSharp.Chat", Chat.GetName().Name);
        Assert.Equal("TensorSharp.Server", Server.GetName().Name);
    }

    [Fact]
    public void TheChatLibrary_DoesNotReferenceAspNetCore()
    {
        string[] strays = Chat.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToArray();

        Assert.True(strays.Length == 0,
            "TensorSharp.Chat references ASP.NET Core:\n  " + string.Join("\n  ", strays));
    }

    [Fact]
    public void TheChatLibrary_DoesNotReferenceDistributedOrTheGpuBackends()
    {
        // Distributed references Backends.Cuda; neither can be linked into a net10.0-ios
        // app. Multi-node tensor parallelism reaches the pipeline through
        // ModelService.TensorParallelGroupFactory instead, and the backend availability
        // probes through BackendCatalogProbes in the Server.
        foreach (string forbidden in new[] { "TensorSharp.Distributed", "TensorSharp.Backends.Cuda", "TensorSharp.Backends.MLX" })
        {
            Assert.DoesNotContain(
                Chat.GetReferencedAssemblies(),
                a => a.Name == forbidden);
        }
    }

    [Fact]
    public void TheServer_BuildsOnTheChatLibrary_AndNotTheOtherWayRound()
    {
        Assert.Contains(Server.GetReferencedAssemblies(), a => a.Name == "TensorSharp.Chat");
        Assert.DoesNotContain(Chat.GetReferencedAssemblies(), a => a.Name == "TensorSharp.Server");
    }

    [Fact]
    public void TheChatPipeline_LivesInTheChatLibrary()
    {
        // The extraction actually moved the pipeline rather than leaving shims behind.
        foreach (Type t in new[]
        {
            typeof(ModelService),
            typeof(SessionManager),
            typeof(ChatSession),
            typeof(SkillRequestPlan),
            typeof(SkillChatLoop),
            typeof(WebUiChatService),
            typeof(SkillsService),
            typeof(WebUiRequestRejectedException),
            typeof(ServerHostingOptions),
            typeof(WebUiSseEvents),
        })
        {
            Assert.Equal("TensorSharp.Chat", t.Assembly.GetName().Name);
        }
    }

    [Fact]
    public void TheHttpTransport_StaysInTheServer()
    {
        foreach (Type t in new[]
        {
            typeof(WebUiAdapter),
            typeof(SkillsAdapter),
            typeof(SseWriter),
            typeof(ServerOptionsBuilder),
            typeof(BackendCatalogProbes),
            typeof(UploadStaticFiles),
            typeof(DistributedTensorParallel),
        })
        {
            Assert.Equal("TensorSharp.Server", t.Assembly.GetName().Name);
        }
    }
}
