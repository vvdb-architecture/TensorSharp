// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Core.Catalog;

/// <summary>
/// Points the Qwen-Image pipeline at the companion networks this installation actually
/// downloaded.
///
/// <para>
/// A qwen_image GGUF is only the diffusion transformer. The VAE, the Qwen2.5-VL text
/// encoder and its vision projector are separate files, and
/// <c>QwenImageModel</c> finds them by scanning the directory the DiT sits in — which
/// is where <see cref="ModelStore"/> puts them, so those three work by construction.
/// The step-distillation LoRA does not: <c>QwenImageDiT.LoraPath</c> reads
/// <c>TS_QWEN_IMAGE_LORA</c> and scans nothing. Without this, the catalog's
/// 850 MB Lightning checkpoint downloads, sits beside the DiT and is ignored, and every
/// edit runs the full step schedule instead of the four steps the entry promises —
/// which on a phone is the difference between a minute and most of an hour.
/// </para>
/// <para>
/// The desktop server does the same translation from its <c>--qwen-image-*</c> flags
/// (<c>ServerOptionsBuilder.ApplyQwenImageCompanionCliFlags</c>). The app has no flags,
/// so the catalog entry and what is on disk decide instead. Every variable is written
/// on every call, and one whose file is absent is cleared rather than left pointing at
/// the previous model's copy: a stale path is a load that fails with a file name the
/// user has never heard of.
/// </para>
/// </summary>
public static class DiffusionCompanions
{
    /// <summary>The environment variable each companion role is published under, which is
    /// the name <c>QwenImageModel</c> and <c>QwenImageDiT</c> read.</summary>
    private static readonly (CatalogFileRole Role, string Variable)[] Published =
    [
        (CatalogFileRole.Vae, "TS_QWEN_IMAGE_VAE"),
        (CatalogFileRole.TextEncoder, "TS_QWEN_IMAGE_TE"),
        (CatalogFileRole.VisionProjector, "TS_QWEN_IMAGE_MMPROJ"),
        (CatalogFileRole.Lora, "TS_QWEN_IMAGE_LORA"),
    ];

    /// <summary>
    /// Publish <paramref name="model"/>'s installed companions and clear the rest.
    /// Returns what was set, variable to path, so the caller can log it — a startup line
    /// naming the four files is the only place a user can see that the LoRA the download
    /// screen charged them for is the one being used.
    /// </summary>
    /// <param name="model">The selected entry, or null when nothing is selected.</param>
    /// <param name="store">Where this installation keeps its models.</param>
    public static IReadOnlyDictionary<string, string> Publish(CatalogModel? model, ModelStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var published = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((CatalogFileRole role, string variable) in Published)
        {
            string? path = model is null ? null : PathOf(model, role, store);
            Environment.SetEnvironmentVariable(variable, path);
            if (path is not null)
                published[variable] = path;
        }
        return published;
    }

    private static string? PathOf(CatalogModel model, CatalogFileRole role, ModelStore store)
    {
        CatalogFile? file = model.Files.FirstOrDefault(f => f.Role == role);
        if (file is null)
            return null;
        string path = store.PathFor(model, file);
        return File.Exists(path) ? path : null;
    }
}
