// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Collections.Concurrent;
using System.Text.Json;

namespace TensorAgent.Core.Sessions;

/// <summary>
/// Keeps the saved transcript in step with what the page is doing.
///
/// <para>
/// The Web UI holds its history in the page and nowhere else: reload it and the
/// conversation is gone. On a desktop that is fine, because the tab stays open. On a
/// phone the app is suspended and killed constantly, and a chat the user had this
/// morning has to still be there this afternoon, so the transcript is written on this
/// side instead.
/// </para>
/// <para>
/// The binding is the awkward part. The engine's session id and the app's
/// conversation id are different things with different lifetimes — a resumed
/// conversation gets a brand-new engine session, because the model's KV cache did not
/// survive the app being killed — so the two are joined when the session is created
/// and the join is what lets a chat request, which knows only its session, be filed
/// under the right conversation.
/// </para>
/// </summary>
public sealed class ConversationRecorder
{
    private readonly ConversationStore _store;
    private readonly ConcurrentDictionary<string, string> _sessionToConversation = new(StringComparer.Ordinal);

    public ConversationRecorder(ConversationStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Bind an engine session to a conversation, creating the conversation when the
    /// page asked for a new one. Returns the conversation now backing that session.
    /// </summary>
    /// <param name="sessionId">The engine session the page just created.</param>
    /// <param name="requested">The conversation id the page asked to resume, or "new"/null for a fresh one.</param>
    public Conversation Bind(string sessionId, string? requested)
    {
        Conversation? conversation = requested is { Length: > 0 } id && !string.Equals(id, "new", StringComparison.Ordinal)
            ? _store.Load(id)
            : null;
        // An empty conversation is one nobody has typed into yet, so a second session
        // asking for a new chat gets that one rather than another beside it. Without
        // this, every launch of the app leaves a row behind.
        conversation ??= _store.MostRecentEmpty();
        conversation ??= _store.Create();
        _sessionToConversation[sessionId] = conversation.Id;
        return conversation;
    }

    /// <summary>Forget a session that has been disposed. The conversation itself is untouched.</summary>
    public void Release(string sessionId) => _sessionToConversation.TryRemove(sessionId, out _);

    /// <summary>The conversation a session is filed under, or null when it was never bound.</summary>
    public string? ConversationFor(string sessionId)
        => _sessionToConversation.TryGetValue(sessionId, out string? id) ? id : null;

    /// <summary>
    /// Record the answer a turn produced, once it has finished.
    ///
    /// <para>
    /// <see cref="Record"/> alone is not enough, and the gap is the one that hurts on
    /// a phone. It runs when a request arrives, so it saves the history the page sent
    /// — which does not yet contain the reply that request is about to generate. The
    /// reply is only written down when the NEXT request carries it, so a user who
    /// asks a question, reads the answer and switches away loses exactly the answer
    /// they were reading. This closes the turn instead of waiting for another one.
    /// </para>
    /// </summary>
    /// <param name="sessionId">The session the answer belongs to.</param>
    /// <param name="content">The assistant's text, as the page assembled it.</param>
    /// <param name="thinking">Its reasoning, when the model produced any.</param>
    public void Complete(string sessionId, string content, string? thinking = null)
    {
        if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(thinking))
            return;
        try
        {
            string? conversationId = ConversationFor(sessionId);
            if (conversationId is null || _store.Load(conversationId) is not { } conversation)
                return;

            // An answer that arrives twice for the same turn replaces the first: a
            // regenerated reply is a correction, not a second message.
            if (conversation.Messages.Count > 0 && conversation.Messages[^1].Role == "assistant")
                conversation.Messages.RemoveAt(conversation.Messages.Count - 1);

            conversation.Messages.Add(new StoredMessage
            {
                Role = "assistant",
                Content = content,
                Thinking = string.IsNullOrEmpty(thinking) ? null : thinking,
            });
            _store.Save(conversation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Same reasoning as Record: a lost transcript must not cost the answer.
        }
    }

    /// <summary>
    /// Record one accepted chat turn.
    ///
    /// <para>
    /// The whole <c>messages</c> array is written rather than only the newest message,
    /// because the page is the authority on what the conversation contains: it may
    /// have edited a message, dropped one, or started from a resumed transcript. Taking
    /// the array wholesale means the saved copy is whatever the page believes, which is
    /// what the user will see when they come back to it.
    /// </para>
    /// <para>
    /// Failures here are swallowed. This runs on the request path of a turn the user
    /// is waiting for, and losing a saved transcript is a smaller harm than failing the
    /// generation that produced it.
    /// </para>
    /// </summary>
    public void Record(string sessionId, JsonElement body)
    {
        try
        {
            string? conversationId = ConversationFor(sessionId);
            if (conversationId is null)
                return;
            Conversation? conversation = _store.Load(conversationId);
            if (conversation is null)
                return;

            if (body.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
            {
                var replacement = new List<StoredMessage>(messages.GetArrayLength());
                foreach (JsonElement message in messages.EnumerateArray())
                {
                    if (message.Deserialize<StoredMessage>(ConversationStore.Json) is { } stored)
                        replacement.Add(stored);
                }
                conversation.Messages = replacement;
            }
            if (body.TryGetProperty("model", out JsonElement model) && model.GetString() is { Length: > 0 } name)
                conversation.ModelId = name;
            if (body.TryGetProperty("think", out JsonElement think) && think.ValueKind is JsonValueKind.True or JsonValueKind.False)
                conversation.Think = think.GetBoolean();
            if (body.TryGetProperty("skills", out JsonElement skills) && skills.ValueKind == JsonValueKind.Array)
            {
                conversation.Skills.Clear();
                foreach (JsonElement skill in skills.EnumerateArray())
                    if (skill.GetString() is { Length: > 0 } skillName)
                        conversation.Skills.Add(skillName);
            }

            _store.Save(conversation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // See the summary: a lost transcript must not cost the user their answer.
        }
    }
}
