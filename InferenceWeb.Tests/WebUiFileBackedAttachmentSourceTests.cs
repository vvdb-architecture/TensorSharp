// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.

namespace InferenceWeb.Tests;

/// <summary>
/// Guards the metadata-only CSV path in the bundled desktop page. The page is a
/// standalone browser asset, so the test inspects the exact copy shipped beside the
/// server rather than introducing a second JavaScript runtime just for this contract.
/// </summary>
public sealed class WebUiFileBackedAttachmentSourceTests
{
    private static string ReadSendMessage()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        Assert.True(File.Exists(path), $"The TensorSharp.Server Web UI was not copied to the test output: {path}");
        string html = File.ReadAllText(path);
        int start = html.IndexOf("async function sendMessage()", StringComparison.Ordinal);
        int end = html.IndexOf("async function runImageEdit", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Could not isolate sendMessage in the bundled Web UI");
        return html.Substring(start, end - start);
    }

    [Fact]
    public void FileBackedCsv_IsReferencedWithoutBeingInlinedAndKeepsAttachmentMetadata()
    {
        string send = ReadSendMessage();
        const string backed = "} else if (att.mediaType === 'text' && att.fileBacked === true) {";
        const string inline = "} else if (att.mediaType === 'text' && att.textContent) {";
        int backedStart = send.IndexOf(backed, StringComparison.Ordinal);
        int inlineStart = send.IndexOf(inline, StringComparison.Ordinal);

        Assert.True(backedStart >= 0 && inlineStart > backedStart,
            "The file-backed CSV branch must run before the inline-text branch");
        string branch = send.Substring(backedStart, inlineStart - backedStart);
        Assert.Contains("textFilePaths.push(att.file)", branch, StringComparison.Ordinal);
        Assert.Contains("textFileNames.push(att.fileName || att.file)", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("textParts.push", branch, StringComparison.Ordinal);

        Assert.Contains("msg.attachments = attachmentsCopy.map", send, StringComparison.Ordinal);
        Assert.Contains("if (att.fileBacked === true) stored.fileBacked = true", send, StringComparison.Ordinal);
    }
}
