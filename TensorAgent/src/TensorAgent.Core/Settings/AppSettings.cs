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
using System.Text.Json.Serialization;

namespace TensorAgent.Core.Settings;

/// <summary>User-controlled settings. Persisted as JSON; every field has a safe default so a
/// missing or older file still loads.</summary>
public sealed class AppSettings
{
    /// <summary>Catalog id of the model the app loads at start and uses for new chats.</summary>
    [JsonPropertyName("selectedModelId")] public string? SelectedModelId { get; set; }

    /// <summary>Whether the model may run programs and skill scripts (the shell tool,
    /// skills_run). Off means the tools are not even declared to the model.</summary>
    [JsonPropertyName("allowCodeExecution")] public bool AllowCodeExecution { get; set; } = true;

    /// <summary>Whether programs and scripts may reach the network (package installs, HTTP).
    /// Enforced in-process by the shell's builtins and Python's audit hook.</summary>
    /// <summary>
    /// Hosts code may reach when <see cref="AllowNetwork"/> is on. Empty means any
    /// host, which is the default: a list is a narrowing the user opts into, not a
    /// default that would quietly break every fetch the first time someone enables
    /// the network. Matched by exact name or as a parent domain.
    /// </summary>
    [JsonPropertyName("networkHosts")] public List<string> NetworkHosts { get; set; } = new();

    [JsonPropertyName("allowNetwork")] public bool AllowNetwork { get; set; } = false;


    /// <summary>Generation cap sent as maxTokens; the Web UI's server default is 20000,
    /// which is far past what a phone should decode in one turn.</summary>
    [JsonPropertyName("maxTokens")] public int MaxTokens { get; set; } = 2048;

    /// <summary>Default state of the Reasoning toggle for new chats.</summary>
    [JsonPropertyName("thinkByDefault")] public bool ThinkByDefault { get; set; } = false;

    /// <summary>Override of the catalog entry's context length (0 = catalog default).</summary>
    [JsonPropertyName("contextLength")] public int ContextLength { get; set; }

    /// <summary>Skills selected by default for new chats.</summary>
    [JsonPropertyName("defaultSkills")] public List<string> DefaultSkills { get; set; } = new();

    /// <summary>Keep the screen awake while generating.</summary>
    /// <summary>
    /// BCP-47 language the speech recogniser listens in, or empty to follow the
    /// device. iOS recognises ONE language per session -- it does not detect which
    /// one is being spoken -- so a bilingual user has to be able to say which, and
    /// the device language is only ever right for one of them.
    /// </summary>
    [JsonPropertyName("speechLanguage")] public string SpeechLanguage { get; set; } = string.Empty;

    [JsonPropertyName("keepAwakeWhileGenerating")] public bool KeepAwakeWhileGenerating { get; set; } = true;

    /// <summary>Allow model downloads over cellular.</summary>
    [JsonPropertyName("allowCellularDownloads")] public bool AllowCellularDownloads { get; set; } = false;

    /// <summary>Whether the optional projector/draft files are downloaded with a model.</summary>
    [JsonPropertyName("downloadOptionalFiles")] public bool DownloadOptionalFiles { get; set; } = true;

    /// <summary>Per-turn wall-clock limit for a single tool call, seconds.</summary>
    [JsonPropertyName("toolTimeoutSeconds")] public int ToolTimeoutSeconds { get; set; } = 120;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>Loads and saves <see cref="AppSettings"/> atomically at a fixed path.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();

    public string Path { get; }

    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(Path))
                return new AppSettings();
            try
            {
                using FileStream stream = File.OpenRead(Path);
                return JsonSerializer.Deserialize<AppSettings>(stream, Json) ?? new AppSettings();
            }
            catch (JsonException)
            {
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string tmp = Path + ".tmp";
            using (FileStream stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, settings, Json);
            File.Move(tmp, Path, overwrite: true);
        }
    }
}
