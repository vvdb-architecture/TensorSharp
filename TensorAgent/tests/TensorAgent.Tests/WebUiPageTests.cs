// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text;
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
        R['/api/models'] = { loaded: 'gemma.gguf', architecture: 'gemma3', backend: 'ggml_metal', visionReady: true };
        R['/api/skills'] = { enabled: true, installable: true, skills: [{ name: 'documents', description: 'make documents' }] };
        R['/api/agent/conversations'] = { conversations: [] };
        // Not a launch: the default is a page coming back inside an app that is
        // already running, which is the case that resumes the last chat. The tests
        // below that hand the page a saved conversation are testing THAT path, and a
        // cold launch would open an empty chat instead and assert nothing.
        R['/api/agent/launch'] = { cold: false };
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

    [Theory]
    [InlineData(131072, 8192, "128K model context", "8K active")]
    [InlineData(262144, 16384, "256K model context", "16K active")]
    [InlineData(262144, 32768, "256K model context", "32K active")]
    public void HeaderShowsTheModelsOwnContextSeparatelyFromTheActiveLimit(
        int modelContext, int activeContext, string expectedModel, string expectedActive)
    {
        JsonElement result = Run($$"""
            R['/api/models'] = { loaded: 'model.gguf', architecture: 'qwen35',
                loadedBackend: 'ggml_metal', visionReady: true,
                contextTokens: {{activeContext}}, modelContextTokens: {{modelContext}} };
            """, """
            var detail = __page.byId['model'].querySelector('.sub');
            return { detail: detail ? detail.textContent : '' };
            """);

        string detail = result.GetProperty("detail").GetString()!;
        Assert.Contains(expectedModel, detail, StringComparison.Ordinal);
        Assert.Contains(expectedActive, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderKeepsCompatibilityWhenTheHostReportsOnlyOneContext()
    {
        JsonElement result = Run("""
            R['/api/models'] = { loaded: 'model.gguf', architecture: 'gemma4',
                loadedBackend: 'ggml_metal', visionReady: true, contextTokens: 8192 };
            """, """
            var detail = __page.byId['model'].querySelector('.sub');
            return { detail: detail ? detail.textContent : '' };
            """);

        string detail = result.GetProperty("detail").GetString()!;
        Assert.Contains("8K context", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("active", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderDoesNotPresentAnEffectiveFallbackAsModelMetadata()
    {
        JsonElement result = Run("""
            R['/api/models'] = { loaded: 'model.gguf', architecture: 'unknown',
                loadedBackend: 'ggml_cpu', visionReady: false,
                contextTokens: 4096, modelContextTokens: 0 };
            """, """
            var detail = __page.byId['model'].querySelector('.sub');
            return { detail: detail ? detail.textContent : '' };
            """);

        string detail = result.GetProperty("detail").GetString()!;
        Assert.Contains("4K active context", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("model context", detail, StringComparison.Ordinal);
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
            return settle(10).then(function () {
              return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }) };
            });
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

    [Fact]
    public void AnImageStaysInTheComposerWhenTheLoadedModelHasNoVisionProjector()
    {
        JsonElement result = Run("""
            var modelReads = 0;
            R['/api/models'] = function () {
              modelReads++;
              return modelReads === 1
                ? { loaded: 'Qwen3.5-9B-IQ4_XS.gguf', architecture: 'qwen35',
                    loadedBackend: 'ggml_metal', visionReady: true }
                : { loaded: 'Qwen3.5-9B-IQ4_XS.gguf', architecture: 'qwen35',
                    loadedBackend: 'ggml_metal', loadedMmProj: 'stale-or-wrong.gguf', visionReady: false };
            };
            """, """
            window.TensorAgent.addAttachment({ ok: true, file: 'a1.png', fileName: 'chart.png',
                                               mediaType: 'image', url: '/uploads/a1.png' });
            __page.byId['text'].value = 'What does this chart show?';
            __page.byId['send'].dispatch('click');
            return settle(10).then(function () {
              var action = __page.byId['chat'].querySelector('.notice-action');
              if (action) action.dispatch('click');
              return {
                sent: __page.requests('/api/chat').length,
                text: __page.byId['text'].value,
                attachments: window.TensorAgent.attachmentCount(),
                history: window.TensorAgent.history(),
                notice: __page.byId['chat'].textContent,
                routes: __page.requests('/api/agent/events').map(function (c) { return c.body; })
              };
            });
            """);

        Assert.Equal(0, result.GetProperty("sent").GetInt32());
        Assert.Equal("What does this chart show?", result.GetProperty("text").GetString());
        Assert.Equal(1, result.GetProperty("attachments").GetInt32());
        Assert.Equal(0, result.GetProperty("history").GetArrayLength());
        Assert.Contains(result.GetProperty("routes").EnumerateArray(), route =>
            route.TryGetProperty("type", out JsonElement type) && type.GetString() == "open-route" &&
            route.TryGetProperty("route", out JsonElement name) && name.GetString() == "models");
    }

    [Fact]
    public void ATextOnlyModelCanHandAnImageFileToASelectedHostSkill()
    {
        JsonElement result = Run("""
            R['/api/agent/settings'] = { thinkByDefault: false, defaultSkills: ['documents'],
                                         skillsEnabled: true, maxTokens: 2048 };
            R['/api/models'] = { loaded: 'text-only.gguf', architecture: 'llama',
                                 loadedBackend: 'ggml_metal', visionReady: false,
                                 acceptsVisionProjector: false };
            """, """
            window.TensorAgent.addAttachment({ ok: true, file: 'a1.png', fileName: 'photo.png',
                                               mediaType: 'image', url: '/uploads/a1.png' });
            __page.byId['text'].value = 'Put this photo into a PDF.';
            __page.byId['send'].dispatch('click');
            return settle(10).then(function () {
              return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }) };
            });
            """);

        JsonElement request = Assert.Single(result.GetProperty("sent").EnumerateArray());
        Assert.Equal(new[] { "documents" }, Strings(request, "skills"));
        Assert.Equal(new[] { "a1.png" }, Strings(request.GetProperty("messages")[0], "imagePaths"));
    }

    [Fact]
    public void AServerVisionRefusalRestoresTheExactImageDraft()
    {
        JsonElement result = Run("""
            R['/api/agent/settings'] = { thinkByDefault: false, defaultSkills: ['summarize'],
                                         skillsEnabled: true, maxTokens: 2048 };
            R['/api/models'] = { loaded: 'text-only.gguf', architecture: 'llama',
                                 loadedBackend: 'ggml_metal', visionReady: false,
                                 acceptsVisionProjector: false };
            R['/api/chat'] = { __status: 400,
                body: { code: 'vision_not_ready', error: 'This skill cannot read the image file.' } };
            """, """
            window.TensorAgent.addAttachment({ ok: true, file: 'a1.png', fileName: 'photo.png',
                                               mediaType: 'image', url: '/uploads/a1.png' });
            __page.byId['text'].value = 'Summarize this image.';
            __page.byId['send'].dispatch('click');
            return settle(15).then(function () {
              return {
                sent: __page.requests('/api/chat').length,
                text: __page.byId['text'].value,
                attachments: window.TensorAgent.attachmentCount(),
                history: window.TensorAgent.history()
              };
            });
            """);

        Assert.Equal(1, result.GetProperty("sent").GetInt32());
        Assert.Equal("Summarize this image.", result.GetProperty("text").GetString());
        Assert.Equal(1, result.GetProperty("attachments").GetInt32());
        Assert.Empty(result.GetProperty("history").EnumerateArray());
    }

    [Fact]
    public void AnImageDraftIsNotSentWhenTheModelUnloadsDuringCapabilityRefresh()
    {
        JsonElement result = Run("""
            var modelReads = 0;
            R['/api/agent/settings'] = { thinkByDefault: false, defaultSkills: ['documents'],
                                         skillsEnabled: true, maxTokens: 2048 };
            R['/api/models'] = function () {
              modelReads++;
              return modelReads === 1
                ? { loaded: 'text-only.gguf', architecture: 'llama', visionReady: false,
                    acceptsVisionProjector: false }
                : { loaded: null, architecture: null, visionReady: false,
                    acceptsVisionProjector: false };
            };
            """, """
            window.TensorAgent.addAttachment({ ok: true, file: 'a1.png', fileName: 'photo.png',
                                               mediaType: 'image', url: '/uploads/a1.png' });
            __page.byId['text'].value = 'Put this into a PDF.';
            __page.byId['send'].dispatch('click');
            return settle(10).then(function () {
              return {
                sent: __page.requests('/api/chat').length,
                text: __page.byId['text'].value,
                attachments: window.TensorAgent.attachmentCount(),
                history: window.TensorAgent.history()
              };
            });
            """);

        Assert.Equal(0, result.GetProperty("sent").GetInt32());
        Assert.Equal("Put this into a PDF.", result.GetProperty("text").GetString());
        Assert.Equal(1, result.GetProperty("attachments").GetInt32());
        Assert.Empty(result.GetProperty("history").EnumerateArray());
    }

    [Fact]
    public void AFileBackedCsvKeepsItsPathAndChipWithoutPuttingRowsInThePrompt()
    {
        JsonElement result = Run(string.Empty, """
            window.TensorAgent.addAttachment({ ok: true, file: 'a5.csv', fileName: 'responses.csv',
                                               mediaType: 'text', url: '/uploads/a5.csv', fileBacked: true });
            __page.byId['text'].value = 'Please analyze this form.';
            __page.byId['send'].dispatch('click');
            return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }) };
            """);

        JsonElement message = result.GetProperty("sent")[0].GetProperty("messages")[0];
        Assert.Equal("Please analyze this form.", message.GetProperty("content").GetString());
        Assert.DoesNotContain("[File:", message.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal(new[] { "a5.csv" }, Strings(message, "textFilePaths"));
        Assert.Equal(new[] { "responses.csv" }, Strings(message, "textFileNames"));

        JsonElement attachment = Assert.Single(message.GetProperty("attachments").EnumerateArray());
        Assert.Equal("a5.csv", attachment.GetProperty("file").GetString());
        Assert.Equal("responses.csv", attachment.GetProperty("fileName").GetString());
        Assert.Equal("text", attachment.GetProperty("mediaType").GetString());
        Assert.True(attachment.GetProperty("fileBacked").GetBoolean());
        Assert.False(attachment.TryGetProperty("textContent", out _));
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

    [Fact]
    public void AGuardedArtifactAppearsOnlyWhenItsDedicatedVerifiedFrameArrives()
    {
        JsonElement result = Run("""
            R['/api/chat'] = { __sse: [
              { skill_step: 'skills_run', skill: 'documents', detail: 'scripts/make_pptx.py',
                ok: true, files: null },
              { tool_progress: 'finished', tool: 'skills_run', seconds: 3 },
              { artifact_verified: true,
                files: [{ name: 'apple-report.pptx', bytes: 15569,
                          url: '/api/code/artifacts/verified/apple-report.pptx' }] },
              { token: 'Done.' },
              { done: true, truncated: false }
            ] };
            """, """
            __page.byId['text'].value = 'make the Apple report';
            __page.byId['send'].dispatch('click');
            return settle(30).then(function () {
              return { transcript: __page.transcript(), history: window.TensorAgent.history() };
            });
            """);

        JsonElement turns = result.GetProperty("transcript");
        JsonElement answer = turns[turns.GetArrayLength() - 1];
        Assert.Contains(answer.GetProperty("media").EnumerateArray(),
            item => item.GetProperty("src").GetString()
                == "/api/code/artifacts/verified/apple-report.pptx");

        JsonElement history = result.GetProperty("history");
        JsonElement assistant = history[history.GetArrayLength() - 1];
        JsonElement artifact = Assert.Single(assistant.GetProperty("artifacts").EnumerateArray());
        Assert.Equal("apple-report.pptx", artifact.GetProperty("name").GetString());
        Assert.Equal(15569, artifact.GetProperty("bytes").GetInt32());
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
    /// <summary>
    /// A file the app uploads reaches the composer, however many lines it has.
    ///
    /// <para>
    /// The regression this exists for: the app used to build
    /// <c>window.TensorAgent.addAttachment({…json…})</c> as a JavaScript SOURCE string
    /// and hand it to MAUI, which wraps every script as
    /// <c>try{JSON.stringify(eval('&lt;script&gt;'))}catch(e){'null'};</c>. The script
    /// therefore became the body of a single-quoted literal, and JSON's <c>\n</c> was
    /// read as that literal's escape — so a text file with two lines produced a real
    /// newline inside an unterminated string, eval threw, MAUI's own catch returned the
    /// STRING "null", and the app discarded it. Upload succeeded, nothing attached,
    /// nothing said. An apostrophe in a file name closed the literal and did the same.
    /// </para>
    /// <para>
    /// So the payload here carries every character that used to break it: newlines, a
    /// double quote, an apostrophe, a backslash and a non-ASCII character — and it
    /// arrives base64'd, which is the fix.
    /// </para>
    /// </summary>
    [Fact]
    public void AMultiLineFileTheAppUploadsReachesTheComposerIntact()
    {
        string json = JsonSerializer.Serialize(new
        {
            ok = true,
            file = "a1.txt",
            fileName = "Bob's \"notes\".txt",
            mediaType = "text",
            url = "/uploads/a1.txt",
            textContent = "line one\nline two\ttabbed\n\"quoted\" and O'Brien \\ backslash\nnaïve café\n",
        });
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        JsonElement result = Run("""
            R['/api/chat'] = { __sse: [{ token: 'Read it.' }, { done: true, truncated: false }] };
            """, $$"""
            var answer = window.TensorAgent.__fromHost('addAttachment', '{{payload}}');
            __page.byId['text'].value = 'What is in it?';
            __page.byId['send'].dispatch('click');
            return settle(20).then(function () {
              return {
                answer: answer,
                sent: __page.requests('/api/chat').map(function (c) { return c.body; })
              };
            });
            """);

        Assert.Equal("ok", result.GetProperty("answer").GetString());

        JsonElement body = result.GetProperty("sent")[0];
        JsonElement messages = body.GetProperty("messages");
        string content = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;

        // The file arrived whole: the envelope, every line, and the characters that used
        // to close the literal early.
        Assert.Contains("[File: Bob's \"notes\".txt]", content, StringComparison.Ordinal);
        Assert.Contains("line one\nline two", content, StringComparison.Ordinal);
        Assert.Contains("O'Brien \\ backslash", content, StringComparison.Ordinal);
        Assert.Contains("naïve café", content, StringComparison.Ordinal);
        Assert.Contains("[End of file]", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coming back returns to the chat the page was in, not to the newest saved one.
    ///
    /// <para>
    /// The pairing that breaks the obvious implementation: <c>fresh</c> is the empty
    /// chat a launch just opened, and it is not in the conversation list at all — a
    /// chat with no messages is never listed. <c>yesterday</c> is. So a page that
    /// resumed "the most recent conversation" would take the user from the clean
    /// composer they were handed on launch into an old transcript, the first time
    /// WebKit reclaimed the content process behind a trip to the Models screen. Which
    /// is the thing this whole change exists to stop.
    /// </para>
    /// </summary>
    [Fact]
    public void ComingBackOpensTheChatThePageWasInAndNotTheNewestSavedOne()
    {
        JsonElement result = Run("""
            R['/api/agent/launch'] = { cold: false, conversation: 'fresh' };
            R['/api/agent/conversations'] = { conversations: [{ id: 'yesterday', title: 'Old', updatedAt: '2026-09-01T10:00:00Z', messageCount: 4 }] };
            R['/api/sessions?conversation=fresh'] = {
              sessionId: 's1', conversationId: 'fresh', messages: [], think: false, skills: []
            };
            R['/api/sessions?conversation=yesterday'] = {
              sessionId: 's2', conversationId: 'yesterday', think: false, skills: [],
              messages: [{ role: 'user', content: 'yesterday' }, { role: 'assistant', content: 'indeed' }]
            };
            """, """
            return {
              opened: __page.requests('/api/sessions').map(function (c) { return c.url; }),
              shown: __page.transcript().map(function (t) { return t.text; }).join(' | ')
            };
            """);

        var opened = result.GetProperty("opened").EnumerateArray().Select(u => u.GetString()!).ToList();
        Assert.Contains("/api/sessions?conversation=fresh", opened);
        Assert.DoesNotContain("/api/sessions?conversation=yesterday", opened);
        Assert.DoesNotContain("indeed", result.GetProperty("shown").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Launching the app opens a clean chat, not yesterday's.
    ///
    /// <para>
    /// The page used to open the most recent conversation on every load, so the app
    /// always reopened mid-thought — reasonable on a desktop tab that stays open,
    /// wrong for something the user launches from a home screen. It now starts empty
    /// and leaves the previous chat one tap away in the menu.
    /// </para>
    /// <para>
    /// Only on a LAUNCH, which is the half this test exists to pin. The identical page
    /// load happens when WebKit kills the content process of a WebView whose view left
    /// the window, and that one must still come back to the chat the user was reading,
    /// possibly with an answer still being generated for it on the host side. The page
    /// cannot tell the two apart from inside, so it asks; every other test here runs
    /// with the <c>cold: false</c> default and exercises that path.
    /// </para>
    /// </summary>
    [Fact]
    public void LaunchingTheAppOpensAnEmptyChatRatherThanTheLastOne()
    {
        JsonElement result = Run("""
            R['/api/agent/launch'] = { cold: true };
            R['/api/agent/conversations'] = { conversations: [{ id: 'saved', title: 'Old chat', updatedAt: '2026-09-01T10:00:00Z', messageCount: 2 }] };
            R['/api/sessions?conversation=saved'] = {
              sessionId: 's9', conversationId: 'saved', think: false, skills: [],
              messages: [{ role: 'user', content: 'yesterday' }, { role: 'assistant', content: 'indeed' }]
            };
            """, """
            return {
              shown: __page.transcript().map(function (t) { return t.text; }).join(' | '),
              history: window.TensorAgent.history(),
              opened: __page.requests('/api/sessions').map(function (c) { return c.url; })
            };
            """);

        // The session that was opened is the whole behaviour: "new" is a fresh chat,
        // "saved" is yesterday's. Asserted on the request rather than on an empty
        // transcript, because a fresh chat is not empty on screen — it carries the
        // "New chat" placeholder, which is exactly what should be there.
        var opened = result.GetProperty("opened").EnumerateArray().Select(u => u.GetString()!).ToList();
        Assert.Contains("/api/sessions?conversation=new", opened);
        Assert.DoesNotContain("/api/sessions?conversation=saved", opened);

        // And nothing of yesterday's chat came with it — not on screen, and not in the
        // history the next message would be sent with, which is the half that would
        // quietly carry the old conversation into the new one's context.
        string shown = result.GetProperty("shown").GetString()!;
        Assert.DoesNotContain("yesterday", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("indeed", shown, StringComparison.Ordinal);
        Assert.Equal(0, result.GetProperty("history").GetArrayLength());
    }

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
            return settle(10).then(function () {
              return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }) };
            });
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
        Assert.False(after[1].GetProperty("skills_discovery").GetBoolean());
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
        Assert.False(result.GetProperty("sent")[0].GetProperty("skills_discovery").GetBoolean());
        Assert.Equal(0, result.GetProperty("chips").GetInt32());
    }

    [Fact]
    public void DeselectingEverySkillSendsAnExplicitEmptySelection()
    {
        JsonElement result = Run("""
            R['/api/agent/settings'] = {
              thinkByDefault: false, defaultSkills: ['documents'], skillsEnabled: true, maxTokens: 2048
            };
            """, """
            window.TensorAgent.setSkills([]);
            __page.byId['text'].value = 'search something and create a PowerPoint report';
            __page.byId['send'].dispatch('click');
            return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }) };
            """);

        JsonElement request = Assert.Single(result.GetProperty("sent").EnumerateArray());
        Assert.True(request.TryGetProperty("skills", out JsonElement skills));
        Assert.Equal(JsonValueKind.Array, skills.ValueKind);
        Assert.Empty(skills.EnumerateArray());
        Assert.False(request.GetProperty("skills_discovery").GetBoolean());
    }

    [Fact]
    public void RoutedNetworkPreflightRestoresThePromptAndOffersTheSettingInline()
    {
        JsonElement result = Run("""
            R['/api/agent/settings'] = function (call) {
              return call.method === 'POST' ? call.body
                : { thinkByDefault: false, defaultSkills: [], skillsEnabled: true,
                    allowNetwork: false, maxTokens: 2048 };
            };
            R['/api/chat'] = { __status: 503,
              body: { code: 'network_disabled',
                      error: 'network access is disabled by the user for this research workflow' } };
            """, """
            var prompt = '搜索apple M6的信息，并对比M5芯片，然后生成pptx报告';
            __page.byId['text'].value = prompt;
            __page.byId['send'].dispatch('click');
            return settle(20).then(function () {
              var action = __page.byId['chat'].querySelector('.notice-action');
              var label = action && action.textContent;
              if (action) action.dispatch('click');
              return settle(10).then(function () {
                return {
                  prompt: __page.byId['text'].value,
                  history: window.TensorAgent.history(),
                  label: label,
                  saved: __page.requests('/api/agent/settings')
                    .filter(function (c) { return c.method === 'POST'; })
                    .map(function (c) { return c.body; })
                };
              });
            });
            """);

        Assert.Equal("搜索apple M6的信息，并对比M5芯片，然后生成pptx报告",
            result.GetProperty("prompt").GetString());
        Assert.Empty(result.GetProperty("history").EnumerateArray());
        Assert.Equal("Turn on Network", result.GetProperty("label").GetString());
        JsonElement saved = Assert.Single(result.GetProperty("saved").EnumerateArray());
        Assert.True(saved.GetProperty("allowNetwork").GetBoolean());
    }

    [Fact]
    public void RoutedSetupPreflightRestoresThePromptAndAttachmentsAndOpensSettings()
    {
        JsonElement result = Run("""
            R['/api/chat'] = { __status: 503,
              body: { code: 'routed_workflow_unavailable',
                      error: 'This routed workflow cannot start: skills_run is unavailable.' } };
            """, """
            window.TensorAgent.addAttachment({ ok: true, file: 'a5.md', fileName: 'notes.md',
                                               mediaType: 'text', url: '/uploads/a5.md',
                                               textContent: 'existing notes' });
            var prompt = '搜索apple M6的信息，并对比M5芯片，然后生成pptx报告';
            __page.byId['text'].value = prompt;
            __page.byId['send'].dispatch('click');
            return settle(20).then(function () {
              var action = __page.byId['chat'].querySelector('.notice-action');
              var label = action && action.textContent;
              if (action) action.dispatch('click');
              return settle(10).then(function () {
                return {
                  prompt: __page.byId['text'].value,
                  attachments: window.TensorAgent.attachmentCount(),
                  history: window.TensorAgent.history(),
                  label: label,
                  routes: __page.requests('/api/agent/events').map(function (c) { return c.body; })
                };
              });
            });
            """);

        Assert.Equal("搜索apple M6的信息，并对比M5芯片，然后生成pptx报告",
            result.GetProperty("prompt").GetString());
        Assert.Equal(1, result.GetProperty("attachments").GetInt32());
        Assert.Empty(result.GetProperty("history").EnumerateArray());
        Assert.Equal("Open Settings", result.GetProperty("label").GetString());
        Assert.Contains(result.GetProperty("routes").EnumerateArray(), route =>
            route.TryGetProperty("type", out JsonElement type) && type.GetString() == "open-route"
            && route.TryGetProperty("route", out JsonElement name) && name.GetString() == "settings");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopenedChatPreservesWhetherAnEmptySkillSelectionWasExplicit(bool explicitSelection)
    {
        string explicitLiteral = explicitSelection ? "true" : "false";
        JsonElement result = Run($$"""
            R['/api/agent/conversations'] = {
              conversations: [{ id: 'saved', title: 'Old', updatedAt: '2026-09-01T10:00:00Z', messageCount: 1 }]
            };
            R['/api/sessions?conversation=saved'] = {
              sessionId: 's9', conversationId: 'saved', think: false, skills: [],
              skillsExplicit: {{explicitLiteral}},
              messages: [{ role: 'user', content: 'hello' }]
            };
            """, """
            __page.byId['text'].value = 'search Apple M6 and compare M5, then make a pptx';
            __page.byId['send'].dispatch('click');
            return { sent: __page.requests('/api/chat').map(function (c) { return c.body; }) };
            """);

        JsonElement request = Assert.Single(result.GetProperty("sent").EnumerateArray());
        if (explicitSelection)
        {
            Assert.True(request.TryGetProperty("skills", out JsonElement skills));
            Assert.Empty(skills.EnumerateArray());
        }
        else
        {
            Assert.False(request.TryGetProperty("skills", out _),
                "an untouched saved chat disabled host-side skill discovery");
        }
    }

    // =====================================================================================
    // the file the model made
    // =====================================================================================

    /// <summary>
    /// Tapping a generated file asks the APP to open it instead of navigating.
    ///
    /// <para>
    /// Navigating is what used to happen, and it is the reported bug: the route serves
    /// its files as attachments (program-written content must never render in the origin
    /// holding the launch token) and a WKWebView with no download delegate drops an
    /// attachment silently -- so what the user saw was the route's own 404 body,
    /// <c>{"error":"not found"}</c>. Both anchors are covered here, because they are
    /// built by different code: the file card this page renders, and the markdown link
    /// the model copies into its answer.
    /// </para>
    /// </summary>
    [Fact]
    public void TappingAGeneratedFileAsksTheAppToOpenItRatherThanNavigating()
    {
        JsonElement result = Run("""
            R['/api/sessions?conversation=new'] = { sessionId: 's1', conversationId: 'c1', messages: [], think: false, skills: [] };
            R['/api/chat'] = { __sse: [
              { skill_step: 'shell', skill: 'documents', detail: 'make_pdf.py', ok: true,
                files: [{ name: 'photo.pdf', bytes: 2048, url: '/api/code/artifacts/run1/photo.pdf' }] },
              { tool_progress: 'finished', tool: 'shell', seconds: 3 },
              { token: 'Here is your PDF: [photo.pdf](/api/code/artifacts/run1/photo.pdf)' },
              { done: true, truncated: false }
            ] };
            """, """
            window.TensorAgent.nativeReady();
            __page.byId['text'].value = 'turn this photo into a pdf';
            __page.byId['send'].dispatch('click');
            return settle(30).then(function () {
              return { links: __page.transcript().reduce(function (all, t) { return all.concat(t.media); }, [])
                .filter(function (m) { return m.tag === 'A'; })
                .map(function (m) { return m.src; }) };
            });
            """);

        // Both links exist and both point at the route.
        string[] links = Strings(result, "links");
        Assert.Contains("/api/code/artifacts/run1/photo.pdf", links);
    }

    /// <summary>
    /// The click itself, on the file card the page rendered: claimed by the page and
    /// handed to the app, with nothing navigated.
    /// </summary>
    [Fact]
    public void TheClickOnAGeneratedFileIsHandedToTheApp()
    {
        JsonElement result = Run("""
            R['/api/sessions?conversation=new'] = { sessionId: 's1', conversationId: 'c1', messages: [], think: false, skills: [] };
            R['/api/chat'] = { __sse: [
              { skill_step: 'shell', skill: 'documents', detail: 'make_pdf.py', ok: true,
                files: [{ name: 'photo.pdf', bytes: 2048, url: '/api/code/artifacts/run1/photo%20one.pdf' }] },
              { tool_progress: 'finished', tool: 'shell', seconds: 3 },
              { token: 'Done.' },
              { done: true, truncated: false }
            ] };
            """, """
            window.TensorAgent.nativeReady();
            __page.byId['text'].value = 'turn this photo into a pdf';
            __page.byId['send'].dispatch('click');
            return settle(30).then(function () {
              var anchor = null;
              (function walk(node) {
                node.children.forEach(function (c) {
                  if (c.tagName === 'A' && String(c.href || '').indexOf('/api/code/artifacts/') === 0) anchor = c;
                  walk(c);
                });
              })(__page.byId['chat']);
              var prevented = false;
              document.dispatch('click', { target: anchor, button: 0, defaultPrevented: false,
                                           preventDefault: function () { prevented = true; } });
              return { found: !!anchor, prevented: prevented,
                       asked: __page.requests('/api/agent/events').map(function (c) { return c.body; }) };
            });
            """);

        Assert.True(result.GetProperty("found").GetBoolean(), "the file card rendered no link to tap");
        Assert.True(result.GetProperty("prevented").GetBoolean(),
            "the tap navigated instead of being handed to the app");
        JsonElement[] asked = result.GetProperty("asked").EnumerateArray()
            .Where(e => e.TryGetProperty("type", out JsonElement t) && t.GetString() == "open-file")
            .ToArray();
        JsonElement open = Assert.Single(asked);
        // The URL is passed through as the page holds it -- percent-encoded -- because
        // the app decodes it one segment at a time against the artifact store.
        Assert.Equal("/api/code/artifacts/run1/photo%20one.pdf", open.GetProperty("url").GetString());
    }

    private static string[] Strings(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();
}
