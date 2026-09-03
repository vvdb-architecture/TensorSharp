// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using Foundation;
using Microsoft.Extensions.Logging;
using TensorAgent.Maui.Hosting;
using TensorSharp.Models.Media;
using TensorSharp.Models.Media.Apple;

namespace TensorAgent.Maui;

public static class MauiProgram
{
    /// <summary>
    /// The scheduler's own name for the solo-prefill chunk size. Left overridable so a
    /// device experiment can try another value without a rebuild, which is how 1024
    /// was chosen.
    /// </summary>
    private const string SoloPrefillChunkVariable = "TS_SCHED_SOLO_PREFILL_CHUNK";

    public static MauiApp CreateMauiApp()
    {
        // Media before anything else can decode: a photo from Photos is HEIC, a clip from
        // the camera roll is H.264 and a Voice Memo is .m4a, and MediaCodecs starts on
        // managed defaults that read none of those — they throw a "register a platform
        // provider" NotSupportedException instead. TensorSharp.Models has a module
        // initializer that does this when its assembly loads, but that is a side effect of
        // something else happening first; calling it here makes the app's dependency on
        // ImageIO/AVFoundation visible where the app is assembled, and it is idempotent.
        AppleMediaProvider.Register();
        Console.WriteLine($"TensorAgent: media providers {MediaCodecs.Describe()}");

        // Prefill in chunks a phone can actually hold.
        //
        // A solo request prefills up to min(SoloPrefillChunkSize, MaxNumBatchedTokens)
        // tokens in ONE fused pass -- 4096 by default. That default is written for a
        // desktop GPU, where a big chunk is several times faster than splitting one,
        // and it is fatal here: on an iPhone 17 Pro Max, pasting a 140-line document
        // into the chat got as far as "Expanded Gemma4 global attention cache to 8192
        // tokens" and then the app was killed outright -- "App terminated due to
        // signal 9", jetsam, with no answer and no error the user could see.
        //
        // Measured on that device with the same 22 kB paste and gemma-4-E2B on Metal:
        //   default (4096) -> killed by jetsam, no answer
        //   2048           -> survives, correct answer, 50.0 s
        //   1024           -> survives, correct answer, 42.4 s
        //
        // 1024 is not a reluctant compromise: it was FASTER than 2048 here, because on
        // a memory-constrained device the pressure a big chunk creates costs more than
        // the fused pass saves. The desktop reasoning does not transfer, so the phone
        // gets its own value rather than the shared default.
        //
        // Set through the environment because that is the seam SchedulerConfig already
        // reads, and it is read when the engine is constructed -- which happens after
        // this. Managed-to-managed, so SetEnvironmentVariable is enough here (a NATIVE
        // getenv would not see it).
        if (Environment.GetEnvironmentVariable(SoloPrefillChunkVariable) is not { Length: > 0 })
            Environment.SetEnvironmentVariable(SoloPrefillChunkVariable, "1024");
        Console.WriteLine($"TensorAgent: solo prefill chunk {Environment.GetEnvironmentVariable(SoloPrefillChunkVariable)} tokens");
#if DEBUG
        // Debug only, and the only place the iOS media provider is ever executed: the repo's
        // xunit suite is a net10.0 host that cannot load an iOS assembly, so ImageIO and
        // AVFoundation are exercised here against files this device encodes itself, and
        // scripts/verify-sim.sh fails the simulator run if any check reports false. Costs a
        // fraction of a second at launch and nothing at all in a Release build.
        Console.WriteLine("TensorAgent: media probe " + System.Text.Json.JsonSerializer.Serialize(
            MediaProbe.Run(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
#endif

        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // The Web UI is TensorSharp.Server/wwwroot linked into the bundle as
        // webui/ (see the BundleResource item in the csproj).
        string webRoot = Path.Combine(NSBundle.MainBundle.BundlePath, "webui");
        // Console logging is what `simctl launch --console` shows and what a device
        // log capture picks up; there is nowhere else for a phone to log to.
        builder.Logging.AddConsole();
        builder.Services.AddSingleton(sp => new LoopbackWebHost(
            webRoot, sp.GetService<ILoggerFactory>()));
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<Pages.SessionsPage>();
        builder.Services.AddSingleton<Pages.ModelsPage>();
        builder.Services.AddSingleton<Pages.SettingsPage>();
        builder.Services.AddSingleton<Pages.AboutPage>();
        builder.Services.AddSingleton<AppShell>();

        return builder.Build();
    }
}
