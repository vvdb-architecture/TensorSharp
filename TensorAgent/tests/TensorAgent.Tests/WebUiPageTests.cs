// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text.Json;
using TensorAgent.Core.JavaScript;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Tests;

/// <summary>
/// The page, RUN.
///
/// <para>
/// Everything else that guards <c>tensoragent.js</c> reads it as text — does it
/// mention this route, does it parse — and none of that can answer the question
/// that mattered: when a chat with a photo in it is closed and opened again, is the
/// photo there. It was not, for two reasons a source-reading test cannot see. The
/// page sent the model its attachments and told the transcript nothing about them,
/// so nothing was saved; and its own history was flattened to role and text on the
/// way out, so even within one session the second question about a picture was asked
/// with the picture removed.
/// </para>
/// <para>
/// So these load the real script into JavaScriptCore — the same engine family the
/// WebView runs — on top of <c>PageDom.js</c>, a DOM the size of what the script
/// touches, and then do what a person does: attach something, send it, reopen the
/// chat. The assertions are on the request the page made and on the elements it
/// built, because those two are the whole contract between the page, the host and
/// the user.
/// </para>
/// </summary>
[Collection(LivePythonCollection.Name)]
public sealed class WebUiPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tensoragent-page-" + Guid.NewGuid().ToString("N"));
    private readonly JavaScriptCoreEngine _engine = new();

    public WebUiPageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // =====================================================================================
    // running the page
    // =====================================================================================

    private static readonly string Repo = FindRepoRoot();

    private static string PageScript =>
        File.ReadAllText(Path.Combine(Repo, "TensorAgent", "src", "TensorAgent.Core", "WebUi", "tensoragent.js"));

    private static string Dom =>
        File.ReadAllText(Path.Combine(Repo, "TensorAgent", "tests", "TensorAgent.Tests", "PageDom.js"));

    /// <summary>
    /// Load the DOM, the routes a launch needs, the page itself, and then
    /// <paramref name="drive"/> — which does the user's part and prints one JSON
    /// object with whatever it wants asserted on.
    /// </summary>
    private JsonElement Run(string routes, string drive)
    {
        string source = Dom + "\n" + Boot(routes) + "\n" + PageScript + "\n" + Settle(drive);
        var policy = new ExecutionPolicy(
            AllowScripts: true,
            AllowNetwork: false,
            WorkRoot: _root,
            ReadableRoots: Array.Empty<string>(),
            TempRoot: _root)
        {
            DefaultTimeout = TimeSpan.FromSeconds(30),
        };
        var context = new InterpreterContext(_root, new Dictionary<string, string> { ["HOME"] = _root }, policy);

        ExecutionResult result = _engine
            .RunCodeAsync(source, Array.Empty<string>(), context, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.True(result.ExitCode == 0,
            $"the page script failed to run.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");

        int start = result.Stdout.IndexOf("<<RESULT>>", StringComparison.Ordinal);
        Assert.True(start >= 0, "the driver printed no result: " + result.Stdout + result.Stderr);
        return JsonSerializer.Deserialize<JsonElement>(
            result.Stdout[(start + "<<RESULT>>".Length)..].Trim());
    }

    /// <summary>The answers a launch needs before anything a test does can happen.</summary>
    private static string Boot(string routes) => """
        var R = __page.routes;
        R['/api/agent/engine'] = { model: { id: 'gemma', name: 'Gemma', state: 'ready' }, networkDisabledMessage: 'network access is disabled by the user' };
        R['/api/agent/settings'] = { thinkByDefault: false, defaultSkills: [], skillsEnabled: true, maxTokens: 2048 };
        R['/api/models'] = { loaded: 'gemma.gguf', architecture: 'gemma3', backend: 'ggml_metal' };
        R['/api/skills'] = { enabled: true, installable: true, skills: [{ name: 'documents', description: 'make documents' }] };
        R['/api/agent/conversations'] = { conversations: [] };
        R['/api/agent/events'] = {};
        R['/api/sessions?conversation=new'] = { sessionId: 's1', conversationId: 'c1', messages: [], think: false, skills: [] };
        """ + "\n" + routes;

    /// <summary>
    /// Let the page finish booting, then drive it, then print. Everything the page
    /// does is promise-chained off its own boot, so the driver waits by giving the
    /// event loop a few turns rather than by reaching into the page's internals.
    /// </summary>
    private static string Settle(string drive) => """
        function settle(times) {
          return new Promise(function (done) {
            var left = times;
            (function tick() {
              if (left-- <= 0) { done(); return; }
              setTimeout(tick, 1);
            })();
          });
        }
        settle(40)
          .then(function () { return (function () {
        """ + drive + """
          })(); })
          .then(function (value) { return settle(40).then(function () { return value; }); })
          .then(function (value) { console.log('<<RESULT>>' + JSON.stringify(value)); })
          .catch(function (e) { console.log('<<RESULT>>' + JSON.stringify({ error: String((e && e.stack) || e) })); });
        """;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "TensorAgent", "skills")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"no TensorAgent above {AppContext.BaseDirectory}");
    }

    // =====================================================================================
    // what a message with attachments carries
    // =====================================================================================

    /// <summary>
    /// A photo, a recording, a clip and a document, attached and sent.
    ///
    /// <para>
    /// Six lists, and the request needs all of them to be right at once: the vision
    /// encoder reads <c>imagePaths</c> (a clip's frames go in there too), the
    /// transcript and the interpreter read <c>attachments</c>, and a document's text
    /// has to be in the content or the model is answering about a file it never saw.
    /// The one that was missing entirely is <c>attachments</c> — which is why nothing
    /// came back — and the one that was WRONG is <c>filePaths</c>, a name no parser on
    /// the other side has ever read, so every PDF and every clip was silently dropped.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryKindOfAttachmentReachesTheRequestUnderTheNameTheServerReads()
    {
        JsonElement result = Run(string.Empty, """
            window.TensorAgent.addAttachment({ ok: true, file: 'a1.png', fileName: 'cat.png', mediaType: 'image', url: '/uploads/a1.png' });
            window.TensorAgent.addAttachment({ ok: true, file: 'a2.wav', fileName: 'note.wav', mediaType: 'audio', url: '/uploads/a2.wav' });
            window.TensorAgent.addAttachment({ ok: true, file: 'a3.mp4', fileName: 'clip.mp4', mediaType: 'video', url: '/uploads/a3.mp4',
                                               frames: ['a3_0001.png', 'a3_0002.png'] });
            window.TensorAgent.addAttachment({ ok: true, file: 'a4.md', fileName: 'notes.md', mediaType: 'text', url: '/uploads/a4.md',
                                               textContent: 'SECRET-MARKER' });
            __page.byId['text'].value = 'What is in these?';
            __page.byId['send'].dispatch('click');
            return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }) };
            """);

        JsonElement sent = result.GetProperty("sent");
        Assert.Equal(1, sent.GetArrayLength());
        JsonElement message = sent[0].GetProperty("messages")[0];

        Assert.Equal(new[] { "a1.png", "a3_0001.png", "a3_0002.png" }, Strings(message, "imagePaths"));
        Assert.Equal(new[] { "a1.png" }, Strings(message, "stillImagePaths"));
        Assert.Equal(new[] { "a3.mp4" }, Strings(message, "videoFilePaths"));
        Assert.Equal(new[] { "a2.wav" }, Strings(message, "audioPaths"));
        Assert.Equal(new[] { "a4.md" }, Strings(message, "textFilePaths"));
        Assert.Equal(new[] { "notes.md" }, Strings(message, "textFileNames"));
        Assert.True(message.GetProperty("isVideo").GetBoolean());

        // The document's own text, in the message. Without it the model is answering
        // about a file whose name is all it was ever given.
        string content = message.GetProperty("content").GetString()!;
        Assert.Contains("SECRET-MARKER", content, StringComparison.Ordinal);
        Assert.Contains("[File: notes.md]", content, StringComparison.Ordinal);
        Assert.EndsWith("What is in these?", content, StringComparison.Ordinal);

        // And the chips, which is what a reopened chat is rebuilt from.
        JsonElement attachments = message.GetProperty("attachments");
        Assert.Equal(4, attachments.GetArrayLength());
        Assert.Equal("cat.png", attachments[0].GetProperty("fileName").GetString());
        Assert.Equal("image", attachments[0].GetProperty("mediaType").GetString());
        Assert.Equal(new[] { "a3_0001.png", "a3_0002.png" }, Strings(attachments[2], "frames"));

        // The name that was never read by anything, and must not come back.
        Assert.False(message.TryGetProperty("filePaths", out _),
            "filePaths is not a field any parser on the server reads; anything sent under it is dropped");
    }

    /// <summary>
    /// The second question about a picture is asked with the picture.
    ///
    /// <para>
    /// The history was mapped to <c>{role, content}</c> on its way into the body, so
    /// turn two of "what is in this photo / and what colour is the car" reached the
    /// model with no photo attached to turn one. It answers anyway, which is what made
    /// this expensive to notice.
    /// </para>
    /// </summary>
    [Fact]
    public void AFollowUpQuestionStillCarriesTheEarlierTurnsPicture()
    {
        JsonElement result = Run("""
            R['/api/chat'] = { __sse: [{ token: 'A cat.' }, { done: true, truncated: false }] };
            """, """
            window.TensorAgent.addAttachment({ ok: true, file: 'a1.png', fileName: 'cat.png', mediaType: 'image', url: '/uploads/a1.png' });
            __page.byId['text'].value = 'What is this?';
            __page.byId['send'].dispatch('click');
            return settle(20).then(function () {
              __page.byId['text'].value = 'And what colour?';
              __page.byId['send'].dispatch('click');
              return settle(20);
            }).then(function () {
              return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }) };
            });
            """);

        JsonElement sent = result.GetProperty("sent");
        Assert.Equal(2, sent.GetArrayLength());
        JsonElement messages = sent[1].GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal(new[] { "a1.png" }, Strings(messages[0], "imagePaths"));
        Assert.Equal("A cat.", messages[1].GetProperty("content").GetString());
        Assert.Equal("And what colour?", messages[2].GetProperty("content").GetString());
    }

    /// <summary>
    /// A file a turn produced is a link in the transcript AND a line in the history,
    /// so the next message does not delete it from the saved chat.
    /// </summary>
    [Fact]
    public void AFileATurnProducedBecomesALinkAndSurvivesInTheHistory()
    {
        JsonElement result = Run("""
            R['/api/chat'] = { __sse: [
              { skill_step: 'shell', skill: 'documents', detail: 'make_pdf.py', ok: true,
                files: [{ name: 'photo.pdf', bytes: 40960, url: '/api/code/artifacts/r/photo.pdf' }] },
              { tool_progress: 'finished', tool: 'shell', seconds: 3 },
              { token: 'Done.' },
              { done: true, truncated: false }
            ] };
            """, """
            __page.byId['text'].value = 'make a pdf';
            __page.byId['send'].dispatch('click');
            return settle(30).then(function () {
              return { transcript: __page.transcript(), history: window.TensorAgent.history() };
            });
            """);

        JsonElement turns = result.GetProperty("transcript");
        JsonElement answer = turns[turns.GetArrayLength() - 1];
        Assert.Contains(answer.GetProperty("media").EnumerateArray(),
            m => m.GetProperty("src").GetString() == "/api/code/artifacts/r/photo.pdf"
                 && m.GetProperty("text").GetString()!.Contains("photo.pdf", StringComparison.Ordinal));

        JsonElement history = result.GetProperty("history");
        JsonElement assistant = history[history.GetArrayLength() - 1];
        Assert.Equal("photo.pdf", assistant.GetProperty("artifacts")[0].GetProperty("name").GetString());
    }

    // =====================================================================================
    // reopening a saved chat
    // =====================================================================================

    /// <summary>
    /// The whole bug, from the user's side: a saved chat with media in it, reopened.
    ///
    /// <para>
    /// The transcript here is what the host really stores — the same property names,
    /// because <c>StoredMessage</c> is the page's own message shape — and what is
    /// asserted is that each kind of attachment comes back as the thing it is: a
    /// picture as a picture, a recording as something playable, a clip as a clip, a
    /// document as a link that opens it. A row of paperclip labels would satisfy a
    /// weaker test and would not be the file coming back.
    /// </para>
    /// </summary>
    [Fact]
    public void AReopenedChatShowsItsPicturesSoundsClipsAndDocumentsAgain()
    {
        JsonElement result = Run("""
            R['/api/agent/conversations'] = { conversations: [{ id: 'saved', title: 'Old chat', updatedAt: '2026-09-01T10:00:00Z', messageCount: 2 }] };
            R['/api/sessions?conversation=saved'] = {
              sessionId: 's9', conversationId: 'saved', think: false, skills: [],
              messages: [
                { role: 'user',
                  content: '[File: notes.md]\nBODY\n[End of file]\n\nLook at these',
                  imagePaths: ['a1.png', 'a3_0001.png'],
                  stillImagePaths: ['a1.png'],
                  videoFilePaths: ['a3.mp4'],
                  audioPaths: ['a2.wav'],
                  textFilePaths: ['a4.md'],
                  textFileNames: ['notes.md'],
                  isVideo: true,
                  attachments: [
                    { file: 'a1.png', fileName: 'cat.png', mediaType: 'image' },
                    { file: 'a2.wav', fileName: 'note.wav', mediaType: 'audio' },
                    { file: 'a3.mp4', fileName: 'clip.mp4', mediaType: 'video', frames: ['a3_0001.png'] },
                    { file: 'a4.md', fileName: 'notes.md', mediaType: 'text' },
                    { file: 'a5.heic', fileName: 'IMG_1.heic', mediaType: 'image', previewFile: 'a5-preview.png' }
                  ] },
                { role: 'assistant', content: 'Here it is.', thinking: 'hmm',
                  artifacts: [{ name: 'report.pdf', bytes: 2048, url: '/api/code/artifacts/r/report.pdf' }] }
              ]
            };
            """, """
            return { transcript: __page.transcript(), history: window.TensorAgent.history() };
            """);

        JsonElement turns = result.GetProperty("transcript");
        Assert.Equal(2, turns.GetArrayLength());

        JsonElement user = turns[0];
        var media = user.GetProperty("media").EnumerateArray()
            .Select(m => (Tag: m.GetProperty("tag").GetString()!, Src: m.GetProperty("src").GetString()!))
            .ToList();

        Assert.Contains(media, m => m.Tag == "IMG" && m.Src == "/uploads/a1.png");
        Assert.Contains(media, m => m.Tag == "AUDIO" && m.Src == "/uploads/a2.wav");
        Assert.Contains(media, m => m.Tag == "VIDEO" && m.Src == "/uploads/a3.mp4");
        Assert.Contains(media, m => m.Tag == "A" && m.Src == "/uploads/a4.md");
        // A HEIC is shown through the PNG the host wrote beside it: no browser
        // renders HEIC, so pointing at the original is a broken image with a 200.
        Assert.Contains(media, m => m.Tag == "IMG" && m.Src == "/uploads/a5-preview.png");

        // The user's own sentence, not the file they attached, in the bubble. The
        // page renders Markdown into innerHTML, so that is where the words are.
        string bubble = user.GetProperty("html").GetString()!;
        Assert.Contains("Look at these", bubble, StringComparison.Ordinal);
        Assert.DoesNotContain("End of file", bubble, StringComparison.Ordinal);

        // The file the turn produced is a link again, not a sentence about a link.
        Assert.Contains(turns[1].GetProperty("media").EnumerateArray(),
            m => m.GetProperty("src").GetString() == "/api/code/artifacts/r/report.pdf");

        // And the history the next request is built from kept every path, so a
        // follow-up question is asked with the same attachments the user can see.
        JsonElement history = result.GetProperty("history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal(new[] { "a1.png", "a3_0001.png" }, Strings(history[0], "imagePaths"));
        Assert.Equal(new[] { "a2.wav" }, Strings(history[0], "audioPaths"));
        Assert.Equal("hmm", history[1].GetProperty("thinking").GetString());
        Assert.Equal("report.pdf", history[1].GetProperty("artifacts")[0].GetProperty("name").GetString());
    }

    /// <summary>
    /// Reopening a chat and then saying something else does not erase what the host
    /// saved about it.
    ///
    /// <para>
    /// The host writes the whole <c>messages</c> array it is sent over the stored
    /// copy, on the reasonable ground that the page is the authority on what the
    /// conversation contains. That makes any field the page forgets a field the next
    /// message DELETES from disk — so the answer's reasoning and the PDF it produced
    /// survive being read and are then thrown away by the follow-up question. This is
    /// the round trip, in one test.
    /// </para>
    /// </summary>
    [Fact]
    public void TheNextMessageDoesNotWipeTheAttachmentsAndFilesAlreadySaved()
    {
        JsonElement result = Run("""
            R['/api/agent/conversations'] = { conversations: [{ id: 'saved', title: 'Old', updatedAt: '2026-09-01T10:00:00Z', messageCount: 2 }] };
            R['/api/sessions?conversation=saved'] = {
              sessionId: 's9', conversationId: 'saved', think: false, skills: [],
              messages: [
                { role: 'user', content: 'Look', imagePaths: ['a1.png'], stillImagePaths: ['a1.png'],
                  attachments: [{ file: 'a1.png', fileName: 'cat.png', mediaType: 'image' }] },
                { role: 'assistant', content: 'A cat.', thinking: 'hmm',
                  artifacts: [{ name: 'report.pdf', bytes: 2048, url: '/api/code/artifacts/r/report.pdf' }] }
              ]
            };
            """, """
            __page.byId['text'].value = 'Anything else?';
            __page.byId['send'].dispatch('click');
            return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }) };
            """);

        JsonElement messages = result.GetProperty("sent")[0].GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal(new[] { "a1.png" }, Strings(messages[0], "imagePaths"));
        Assert.Equal("cat.png", messages[0].GetProperty("attachments")[0].GetProperty("fileName").GetString());
        Assert.Equal("hmm", messages[1].GetProperty("thinking").GetString());
        Assert.Equal("report.pdf", messages[1].GetProperty("artifacts")[0].GetProperty("name").GetString());
    }

    // =====================================================================================
    // the skills switch
    // =====================================================================================

    /// <summary>
    /// Off means off: the setting is saved, the selection is dropped, and the next
    /// message names no skill.
    /// </summary>
    [Fact]
    public void TurningSkillsOffSavesTheSettingAndSendsNoSkills()
    {
        JsonElement result = Run("""
            R['/api/agent/settings'] = function (call) {
              return call.method === 'POST' ? call.body
                : { thinkByDefault: false, defaultSkills: ['documents'], skillsEnabled: true, maxTokens: 2048 };
            };
            """, """
            var before = [];
            __page.byId['text'].value = 'first';
            __page.byId['send'].dispatch('click');
            before = __page.requests('/api/chat').map(function (c) { return c.body; });

            __page.byId['skills-master'].checked = false;
            __page.byId['skills-master'].dispatch('change');
            return settle(20).then(function () {
              __page.byId['text'].value = 'second';
              __page.byId['send'].dispatch('click');
              return {
                before: before,
                after: __page.requests('/api/chat').map(function (c) { return c.body; }),
                saved: __page.requests('/api/agent/settings').filter(function (c) { return c.method === 'POST'; })
                         .map(function (c) { return c.body; }),
                chips: __page.byId['skillchips'].children.length,
                listClass: __page.byId['skills-list'].className,
              };
            });
            """);

        // With it on, the chat request names the default skill.
        Assert.Equal(new[] { "documents" }, Strings(result.GetProperty("before")[0], "skills"));

        // Saved as a setting, not merely held in the page: it is a preference, and it
        // is what the host reads to build no skill plan at all.
        JsonElement saved = result.GetProperty("saved");
        Assert.True(saved.GetArrayLength() >= 1, "turning the switch off saved nothing");
        Assert.False(saved[saved.GetArrayLength() - 1].GetProperty("skillsEnabled").GetBoolean());

        JsonElement after = result.GetProperty("after");
        Assert.Equal(2, after.GetArrayLength());
        Assert.False(after[1].TryGetProperty("skills", out _),
            "the message after the switch went off still named a skill");
        Assert.Equal(0, result.GetProperty("chips").GetInt32());
        Assert.Contains("off", result.GetProperty("listClass").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A chat saved when skills were on does not switch them back on for itself.
    /// The setting is the wider fact; a stored selection is a memory of one chat.
    /// </summary>
    [Fact]
    public void ASavedChatDoesNotReviveSkillsThatWereTurnedOff()
    {
        JsonElement result = Run("""
            R['/api/agent/settings'] = { thinkByDefault: false, defaultSkills: [], skillsEnabled: false, maxTokens: 2048 };
            R['/api/skills'] = { enabled: false, installable: true, skills: [{ name: 'documents', description: 'd' }] };
            R['/api/agent/conversations'] = { conversations: [{ id: 'saved', title: 'Old', updatedAt: '2026-09-01T10:00:00Z', messageCount: 1 }] };
            R['/api/sessions?conversation=saved'] = {
              sessionId: 's9', conversationId: 'saved', think: false, skills: ['documents'],
              messages: [{ role: 'user', content: 'hello' }]
            };
            """, """
            __page.byId['text'].value = 'again';
            __page.byId['send'].dispatch('click');
            return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }),
                     chips: __page.byId['skillchips'].children.length };
            """);

        Assert.Equal(1, result.GetProperty("sent").GetArrayLength());
        Assert.False(result.GetProperty("sent")[0].TryGetProperty("skills", out _),
            "a saved chat re-selected a skill after the feature was switched off");
        Assert.Equal(0, result.GetProperty("chips").GetInt32());
    }

    private static string[] Strings(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();
}
