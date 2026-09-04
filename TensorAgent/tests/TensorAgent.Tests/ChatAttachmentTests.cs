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
using System.Text.Json;
using TensorSharp.AgentHost.Skills;
using TensorSharp.Runtime;

namespace TensorAgent.Tests;

/// <summary>
/// What an attached file is to the two things that consume one: the model, and the
/// interpreter a program runs in.
///
/// <para>
/// The second half was missing, and the failure it produced looked like a stupid
/// model. Asked to turn a photo into a PDF, the app showed the picture to the vision
/// encoder and staged NOTHING into the working directory — only text uploads were
/// ever staged — so the model was told, truthfully, that it could run programs, and
/// then spent the turn guessing at a filename that was never going to exist.
/// </para>
/// <para>
/// The parser and the collector are reached by reflection because both are internal
/// to <c>TensorSharp.Chat</c>. That is worth the ugliness here: these two functions
/// are the entire contract, they are pure, and testing them through a loaded model
/// costs three minutes and cannot say which of them was wrong.
/// </para>
/// </summary>
public sealed class ChatAttachmentTests : IDisposable
{
    private readonly string _uploads = Path.Combine(Path.GetTempPath(), "tensoragent-att-" + Guid.NewGuid().ToString("N"));

    public ChatAttachmentTests() => Directory.CreateDirectory(_uploads);

    public void Dispose()
    {
        try { Directory.Delete(_uploads, true); } catch { }
    }

    private static readonly Assembly Chat = typeof(TensorSharp.Chat.WebUiChatService).Assembly;

    private static List<ChatMessage> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Type parser = Chat.GetType("TensorSharp.Server.RequestParsers.ChatMessageParser")!;
        return (List<ChatMessage>)parser
            .GetMethod("ParseWebUi", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { document.RootElement })!;
    }

    private static string? Resolve(List<ChatMessage> messages, string uploadRoot)
    {
        Type parser = Chat.GetType("TensorSharp.Server.RequestParsers.ChatMessageParser")!;
        return (string?)parser
            .GetMethod("ResolveAttachmentPaths", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { messages, uploadRoot });
    }

    private static IReadOnlyList<CodeInputFile> Collect(List<ChatMessage> messages) =>
        (IReadOnlyList<CodeInputFile>)typeof(TensorSharp.Chat.WebUiChatService)
            .GetMethod("CollectCodeInputFiles", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { messages })!;

    private string Upload(string name)
    {
        string path = Path.Combine(_uploads, name);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>
    /// A photo the user attached is a file the model can open, under the name the
    /// user knows it by.
    /// </summary>
    [Fact]
    public void AnAttachedPhotoIsStagedForTheInterpreterAndNotOnlyForTheEncoder()
    {
        Upload("g1.png");
        List<ChatMessage> messages = Parse("""
            [{ "role": "user", "content": "turn this into a pdf",
               "imagePaths": ["g1.png"], "stillImagePaths": ["g1.png"],
               "attachments": [{ "file": "g1.png", "fileName": "IMG_0004.png", "mediaType": "image" }] }]
            """);
        Assert.Null(Resolve(messages, _uploads));

        IReadOnlyList<CodeInputFile> staged = Collect(messages);
        CodeInputFile only = Assert.Single(staged);
        Assert.Equal("IMG_0004.png", only.Name);
        Assert.Equal(Path.Combine(_uploads, "g1.png"), only.SourcePath);
    }

    /// <summary>
    /// Every kind, once each, under the user's own names — and a frame the server
    /// extracted is not one of them.
    /// </summary>
    [Fact]
    public void EveryAttachmentIsStagedOnceAndDerivedFramesAreNot()
    {
        foreach (string name in new[] { "g1.png", "g2.wav", "g3.mp4", "g4.md", "g3_0001.png" })
            Upload(name);

        List<ChatMessage> messages = Parse("""
            [{ "role": "user", "content": "[File: notes.md]\nBODY\n[End of file]\n\nlook",
               "imagePaths": ["g1.png", "g3_0001.png"],
               "audioPaths": ["g2.wav"],
               "videoFilePaths": ["g3.mp4"],
               "textFilePaths": ["g4.md"], "textFileNames": ["notes.md"],
               "isVideo": true,
               "attachments": [
                 { "file": "g1.png", "fileName": "cat.png", "mediaType": "image" },
                 { "file": "g2.wav", "fileName": "note.wav", "mediaType": "audio" },
                 { "file": "g3.mp4", "fileName": "clip.mp4", "mediaType": "video" },
                 { "file": "g4.md", "fileName": "notes.md", "mediaType": "text" }
               ] }]
            """);
        Assert.Null(Resolve(messages, _uploads));

        Assert.Equal(
            new[] { "cat.png", "clip.mp4", "note.wav", "notes.md" },
            Collect(messages).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// A client that sends no <c>attachments</c> — the desktop page, and any build
    /// older than this — keeps exactly the behaviour it had.
    /// </summary>
    [Fact]
    public void AClientThatSendsNoAttachmentsArrayStillGetsItsDocumentsStaged()
    {
        Upload("g4.md");
        List<ChatMessage> messages = Parse("""
            [{ "role": "user", "content": "summarise",
               "textFilePaths": ["g4.md"], "textFileNames": ["notes.md"] }]
            """);
        Assert.Null(Resolve(messages, _uploads));

        CodeInputFile only = Assert.Single(Collect(messages));
        Assert.Equal("notes.md", only.Name);
    }

    /// <summary>
    /// An attachment path is confined to the upload directory like every other one.
    /// It is a client-supplied path that now reaches the filesystem, so the check
    /// that was already applied to images has to cover it too.
    /// </summary>
    [Fact]
    public void AnAttachmentPathOutsideTheUploadDirectoryIsRefused()
    {
        List<ChatMessage> messages = Parse("""
            [{ "role": "user", "content": "read this",
               "attachments": [{ "file": "../../etc/passwd", "fileName": "passwd", "mediaType": "text" }] }]
            """);
        Assert.NotNull(Resolve(messages, _uploads));
    }

    /// <summary>
    /// The same file attached twice keeps its first copy, so a name cannot change
    /// which bytes a program reads halfway through a conversation.
    /// </summary>
    [Fact]
    public void ARepeatedNameKeepsTheFileItFirstMeant()
    {
        Upload("g1.png");
        Upload("g9.png");
        List<ChatMessage> messages = Parse("""
            [{ "role": "user", "content": "one",
               "attachments": [{ "file": "g1.png", "fileName": "photo.png", "mediaType": "image" }] },
             { "role": "assistant", "content": "ok" },
             { "role": "user", "content": "two",
               "attachments": [{ "file": "g9.png", "fileName": "photo.png", "mediaType": "image" }] }]
            """);
        Assert.Null(Resolve(messages, _uploads));

        CodeInputFile only = Assert.Single(Collect(messages));
        Assert.Equal(Path.Combine(_uploads, "g1.png"), only.SourcePath);
    }
}
