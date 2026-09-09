// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text.Json.Serialization;

namespace TensorAgent.Sharing;

/// <summary>
/// The serializer metadata for <see cref="SharePayload"/>, generated at compile time.
///
/// <para>
/// Source generation rather than reflection because of where the other half of this
/// contract runs. An iOS app extension is trimmed — the SDK refuses to build one
/// otherwise (<c>_MustTrim</c> is forced for any app-extension project with a runtime
/// identifier) — and reflection-based <c>System.Text.Json</c> in a trimmed assembly
/// fails at RUN time, in the share sheet, on a device, with a payload that serializes
/// to <c>{}</c> and no build warning anywhere. The app itself turns reflection back on
/// with <c>JsonSerializerIsReflectionEnabledByDefault</c>; the extension deliberately
/// does not need to, and neither project can silently lose this by changing a setting.
/// </para>
/// <para>
/// The generated context also owns the options used by both processes, so the two
/// sides cannot drift on indentation, null handling, or case sensitivity.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = true)]
[JsonSerializable(typeof(SharePayload))]
[JsonSerializable(typeof(ShareItem))]
internal sealed partial class SharePayloadJsonContext : JsonSerializerContext
{
}
