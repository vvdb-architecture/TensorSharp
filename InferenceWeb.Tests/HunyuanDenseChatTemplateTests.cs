// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
using System.Collections.Generic;
using TensorSharp.AgentHost.Skills;
using TensorSharp.Runtime;

namespace InferenceWeb.Tests;

public class HunyuanDenseChatTemplateTests
{
    [Fact]
    public void UserTurn_GetsBosAndGenerationPrompt()
    {
        string prompt = ChatTemplate.RenderHunyuanDense(
        [
            new ChatMessage { Role = "user", Content = "Hello" },
        ]);

        Assert.Equal("<｜hy_begin▁of▁sentence｜><｜hy_User｜>Hello<｜hy_Assistant｜>", prompt);
    }

    [Fact]
    public void SystemThenUser_MatchesOfficialHyMt2Jinja()
    {
        string prompt = ChatTemplate.RenderHunyuanDense(
        [
            new ChatMessage { Role = "system", Content = "You are a translator." },
            new ChatMessage { Role = "user", Content = "Hello" },
        ]);

        Assert.Equal(
            "<｜hy_begin▁of▁sentence｜>You are a translator.<｜hy_place▁holder▁no▁3｜><｜hy_User｜>Hello<｜hy_Assistant｜>",
            prompt);
    }

    [Fact]
    public void AssistantHistory_ClosesWithPlaceholderTwo()
    {
        string prompt = ChatTemplate.RenderHunyuanDense(
        [
            new ChatMessage { Role = "user", Content = "Hi" },
            new ChatMessage { Role = "assistant", Content = "你好" },
            new ChatMessage { Role = "user", Content = "Thanks" },
        ]);

        Assert.Equal(
            "<｜hy_begin▁of▁sentence｜><｜hy_User｜>Hi<｜hy_Assistant｜>你好<｜hy_place▁holder▁no▁2｜><｜hy_User｜>Thanks<｜hy_Assistant｜>",
            prompt);
    }

    [Fact]
    public void WithoutGenerationPrompt_EndsWithPlaceholderEight()
    {
        string prompt = ChatTemplate.RenderHunyuanDense(
        [
            new ChatMessage { Role = "user", Content = "Hello" },
        ], addGenerationPrompt: false);

        Assert.Equal("<｜hy_begin▁of▁sentence｜><｜hy_User｜>Hello<｜hy_place▁holder▁no▁8｜>", prompt);
    }

    [Fact]
    public void Protocol_BypassesJinjaAndOffersNoTools()
    {
        ChatProtocol protocol = ChatProtocolRegistry.For("hunyuan-dense");
        Assert.NotNull(protocol);
        Assert.True(protocol.PreferOwnRenderer(new ChatRenderRequest([], true, "hunyuan-dense", null, false)));
        Assert.False(protocol.RendersToolDeclarations);
        Assert.False(protocol.RendersToolResultMessages);
        Assert.False(SkillCapabilities.For("hunyuan-dense").ToolsRendered);
    }
}
