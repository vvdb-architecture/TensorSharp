// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Sharing;

/// <summary>
/// The names the app and its share extension must spell identically.
///
/// <para>
/// They live here, in the one assembly both processes load, for the reason every
/// duplicated constant eventually earns: a mismatched App Group identifier does not
/// fail, it returns <c>nil</c> from
/// <c>containerURLForSecurityApplicationGroupIdentifier:</c> — with no exception, no log
/// line and no build warning — and the feature then quietly works only for text.
/// </para>
/// <para>
/// The identifier also appears in two places C# cannot reach: the app's and the
/// extension's <c>CustomEntitlements</c> in their csproj files. A drift test in
/// <c>InferenceWeb.Tests</c> reads all three and fails when they disagree, because that
/// is the only way a build can notice.
/// </para>
/// </summary>
public static class ShareContainer
{
    /// <summary>The App Group both bundles are entitled to.</summary>
    public const string GroupIdentifier = "group.ai.tensorsharp.tensoragent";

    /// <summary>The subdirectory, inside the group container, that holds the envelopes.</summary>
    public const string InboxDirectoryName = "Library/Application Support/IncomingShares";

    /// <summary>One coalescing, content-free notification for any waiting shares.</summary>
    public const string NotificationIdentifier = "tensoragent-share-ready";
}
