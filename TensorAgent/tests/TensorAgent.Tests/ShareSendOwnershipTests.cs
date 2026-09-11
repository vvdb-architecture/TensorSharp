// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.

using System.Text.Json;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Sharing;
using TensorAgent.Sharing;
using TensorSharp.AgentHost.CodeExec;
using TensorSharp.Chat;

namespace TensorAgent.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ShareSendOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tensoragent-share-owner-" + Guid.NewGuid().ToString("N"));
    private readonly AgentAppHost _host;

    public ShareSendOwnershipTests()
    {
        _host = new AgentAppHost(new AgentPaths(
            Path.Combine(_root, "data"), Path.Combine(_root, "cache")));
    }

    public void Dispose()
    {
        _host.Dispose();
        CodeEnvironment.Reset();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static PendingShare Pending(string id) => new(
        id,
        "What can you tell me about this? shared text",
        Array.Empty<object>(),
        Array.Empty<string>(),
        "Shared item",
        NewChat: true,
        AutoSend: false);

    private static JsonElement Request(string id) => JsonSerializer.SerializeToElement(new
    {
        messages = new[] { new { role = "user", content = "shared text" } },
        shareIds = new[] { id },
    });

    [Fact]
    public void SendLeaseWins_DiscardIsRejectedUntilLeaseEnds()
    {
        string id = ShareIds.New();
        Assert.True(_host.Shares.Offer(Pending(id)));

        using (IDisposable lease = _host.Chat.AcquireChatRequestLease(Request(id)))
        {
            Assert.False(_host.DiscardPendingShare(id));
            Assert.Equal(1, _host.Shares.PendingCount);
        }

        Assert.True(_host.DiscardPendingShare(id));
        Assert.Equal(0, _host.Shares.PendingCount);
    }

    [Fact]
    public void DiscardWins_LaterSendLeaseIsRejected()
    {
        string id = ShareIds.New();
        Assert.True(_host.Shares.Offer(Pending(id)));
        Assert.True(_host.DiscardPendingShare(id));

        WebUiRequestRejectedException ex = Assert.Throws<WebUiRequestRejectedException>(
            () => _host.Chat.AcquireChatRequestLease(Request(id)));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("shared draft", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _host.Shares.PendingCount);
    }

    [Fact]
    public void OneTurnCannotLeaseOrConsumeTwoIndependentShares()
    {
        string first = ShareIds.New();
        string second = ShareIds.New();
        Assert.True(_host.Shares.Offer(Pending(first)));
        Assert.True(_host.Shares.Offer(Pending(second)));
        JsonElement request = JsonSerializer.SerializeToElement(new
        {
            messages = new[] { new { role = "user", content = "combined by a stale page" } },
            shareIds = new[] { first, second },
        });

        WebUiRequestRejectedException ex = Assert.Throws<WebUiRequestRejectedException>(
            () => _host.Chat.AcquireChatRequestLease(request));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("own chat", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, _host.Shares.PendingCount);
        Assert.Equal(first, _host.Shares.Peek()!.Id);
    }
}
