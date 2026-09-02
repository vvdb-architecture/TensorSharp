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

namespace TensorSharp.Chat
{
    /// <summary>
    /// A Web UI request refused before its reply began: the transport answers with
    /// <see cref="StatusCode"/> and <see cref="Payload"/> serialised as the body.
    ///
    /// <para>
    /// The services throw this rather than returning a status because the same
    /// method has to serve an ASP.NET endpoint, an in-process loopback server and a
    /// native UI without those agreeing on a result type — and because a streaming
    /// method has no return value to carry a status in. A stream throws it from its
    /// first <c>MoveNextAsync</c>, before any frame, which is exactly the point at
    /// which the transport still can set a status code.
    /// </para>
    /// <para>
    /// <see cref="Payload"/> is the wire object the Web UI expects for that failure
    /// (<c>{ error }</c>, <c>{ ok = false, error }</c>, or the <c>/v1</c> envelope);
    /// it is never reshaped here so the contract stays byte-identical across hosts.
    /// </para>
    /// </summary>
    public sealed class WebUiRequestRejectedException : Exception
    {
        public WebUiRequestRejectedException(int statusCode, object payload)
            : base(DescribePayload(payload) ?? $"Request rejected with HTTP {statusCode}.")
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            StatusCode = statusCode;
            Payload = payload;
        }

        /// <summary>The HTTP status the transport should answer with.</summary>
        public int StatusCode { get; }

        /// <summary>The JSON body, exactly as the Web UI adapter used to return it.</summary>
        public object Payload { get; }

        // The payloads are the adapters' anonymous objects; nearly all carry an `error`
        // string, which is what a log line or a test wants to see as the message.
        private static string DescribePayload(object payload)
        {
            if (payload == null)
                return null;
            object error = payload.GetType().GetProperty("error")?.GetValue(payload);
            return error switch
            {
                string text => text,
                null => null,
                var other => other.ToString(),
            };
        }
    }
}
