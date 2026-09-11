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
/// The share id: 32 lowercase hex characters, and nothing else ever.
///
/// <para>
/// It is worth its own type because the id is used as a DIRECTORY NAME and arrives
/// from another process through a shared container. <c>ConversationStore.IsValidId</c> exists for the same
/// reason and this is the same rule; a share id that could contain a separator or a
/// dot-dot would turn "open the share the extension left" into "read the directory
/// the caller names", and the app's own conversations and settings are one level up.
/// </para>
/// </summary>
public static class ShareIds
{
    /// <summary>Length of a valid id, in characters.</summary>
    public const int Length = 32;

    /// <summary>A fresh id.</summary>
    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>Whether <paramref name="id"/> may be used as a directory name here.</summary>
    public static bool IsValid(string? id)
    {
        if (id is null || id.Length != Length)
            return false;
        foreach (char c in id)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!hex)
                return false;
        }
        return true;
    }
}
