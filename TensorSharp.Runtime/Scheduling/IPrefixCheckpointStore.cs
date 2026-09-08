// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
using System;
using System.IO;

namespace TensorSharp.Runtime.Scheduling
{
    /// <summary>
    /// Where a shared-prefix checkpoint outlives the process.
    ///
    /// <para>
    /// The executor checkpoints the model's state where the prompt every conversation
    /// shares ends (see <see cref="IBatchedPagedModel.TryCheckpointActiveCache"/>) and
    /// starts every later chat from a copy. That copy is made once per process, by
    /// whichever request first crosses the boundary — on a phone, the warm-up after a
    /// load, which prefills several thousand tokens of system prompt, tools and skills
    /// at a hundred or two tokens a second: forty seconds for a 9B model, two and a
    /// half minutes for a 27B one, and a first message sent before it is done pays the
    /// same. A store keeps the checkpoint's bytes between launches, so the next process
    /// restores it in a fraction of a second instead of prefilling it again. The same
    /// idea as llama.cpp's prompt-cache files (<c>llama_state_seq_save_file</c>), scoped
    /// to the one prefix a host cares about.
    /// </para>
    /// <para>
    /// The executor calls both members on the engine thread. A store must never throw
    /// out of them: a checkpoint that cannot be read is re-prefilled and one that
    /// cannot be written is simply not kept, and either is a log line, not a failure.
    /// Bytes come from and go back to the SAME model (<paramref name="modelFingerprint"/>
    /// names it, and the model validates its own payload); a store only frames them.
    /// </para>
    /// </summary>
    public interface IPrefixCheckpointStore
    {
        /// <summary>
        /// Open the saved checkpoint taken after exactly <paramref name="prefixTokens"/>
        /// on the model <paramref name="modelFingerprint"/>. On success the stream is
        /// positioned at the model's payload and the caller disposes it. False when
        /// there is none (or it is unreadable, which the store treats as none).
        /// </summary>
        bool TryOpen(string modelFingerprint, ReadOnlySpan<int> prefixTokens, out Stream payload);

        /// <summary>
        /// Keep a checkpoint: <paramref name="writePayload"/> is invoked once with a
        /// stream to write the model's bytes into, and the store makes the result
        /// durable under (<paramref name="modelFingerprint"/>, <paramref name="prefixTokens"/>).
        /// False when it could not be kept (including when the writer threw); nothing
        /// half-written is ever handed back by <see cref="TryOpen"/>.
        /// </summary>
        bool Save(string modelFingerprint, ReadOnlySpan<int> prefixTokens, Action<Stream> writePayload);
    }
}
