// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using AVFoundation;
using Foundation;
using Speech;

namespace TensorAgent.Maui.Platforms.iOS;

/// <summary>
/// Voice input, straight onto Apple's own speech recogniser.
///
/// <para>
/// This is deliberately not a toolkit package. The app is fully ahead-of-time
/// compiled and aggressively trimmed, and the whole feature is one recogniser, one
/// audio tap and one request object — sixty lines against a dependency tree, for
/// something that has to keep working when the trimmer is at its most aggressive.
/// </para>
/// <para>
/// It asks for on-device recognition where the device offers it. That is not a
/// performance choice: dictation for a private, offline chat app must not send the
/// user's speech to a server, and a recogniser that quietly falls back to the
/// network would do exactly that. Where on-device recognition is unavailable the
/// caller is told, rather than being upgraded to a server round trip it did not ask
/// for.
/// </para>
/// </summary>
internal sealed class Dictation : IDisposable
{
    private readonly AVAudioEngine _engine = new();
    private SFSpeechRecognizer? _recognizer;
    private readonly string _language;

    /// <param name="language">BCP-47 tag such as "en-US" or "zh-CN"; empty follows the device.</param>
    public Dictation(string? language = null) => _language = language ?? string.Empty;

    private SFSpeechAudioBufferRecognitionRequest? _request;
    private SFSpeechRecognitionTask? _task;
    private TaskCompletionSource<string>? _completion;

    /// <summary>Whether this device has a recogniser for the current language at all.</summary>
    public static bool IsSupported => new SFSpeechRecognizer(NSLocale.CurrentLocale) is { Available: true };

    /// <summary>Ask for the two permissions this needs, and say which one was refused.</summary>
    /// <summary>
    /// Marker the caller can look for to know the refusal is permanent and only the
    /// user can lift it, so it can offer Settings instead of repeating itself.
    /// </summary>
    public const string DeniedMarker = "[denied]";

    /// <summary>
    /// Ask for the two permissions dictation needs, or say why it cannot have them.
    ///
    /// <para>
    /// Two, not one: speech recognition and the microphone are separate grants, and a
    /// build that asks for only the first fails later inside the audio session with an
    /// error that says nothing about permissions.
    /// </para>
    /// <para>
    /// A permission already DENIED is reported differently from one not yet asked for.
    /// iOS shows its prompt once; after that RequestAuthorization returns the stored
    /// answer without showing anything, so a user who said no by reflex sees the
    /// button fail forever with no way back. That case names Settings, and carries
    /// <see cref="DeniedMarker"/> so the caller can offer to open it.
    /// </para>
    /// </summary>
    public static async Task<string?> RequestPermissionsAsync()
    {
        // RunContinuationsAsynchronously on both: iOS delivers these callbacks on its
        // own threads, and without it everything after the await -- including starting
        // the AVAudioSession -- continues on that thread instead of the main one.
        if (SFSpeechRecognizer.AuthorizationStatus is SFSpeechRecognizerAuthorizationStatus.Denied
            or SFSpeechRecognizerAuthorizationStatus.Restricted)
        {
            return "Speech recognition is turned off for TensorAgent. Turn it on in "
                + "Settings › TensorAgent › Speech Recognition. " + DeniedMarker;
        }

        var speech = new TaskCompletionSource<SFSpeechRecognizerAuthorizationStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SFSpeechRecognizer.RequestAuthorization(speech.SetResult);
        if (await speech.Task != SFSpeechRecognizerAuthorizationStatus.Authorized)
            return "Dictation needs permission to use speech recognition. " + DeniedMarker;

        if (AVAudioApplication.SharedInstance.RecordPermission == AVAudioApplicationRecordPermission.Denied)
        {
            return "The microphone is turned off for TensorAgent. Turn it on in "
                + "Settings › TensorAgent › Microphone. " + DeniedMarker;
        }

        var microphone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        AVAudioApplication.RequestRecordPermission(microphone.SetResult);
        return await microphone.Task ? null : "Dictation needs permission to use the microphone. " + DeniedMarker;
    }

    /// <summary>
    /// Listen until <see cref="Stop"/> is called, then return everything recognised.
    /// Partial results are discarded on purpose: appending them to the composer would
    /// rewrite the user's own text while they were still speaking.
    /// </summary>
    public async Task<string> ListenAsync()
    {
        // One locale per session: iOS does not detect the spoken language, so the
        // caller's choice decides. Empty follows the device, which is right until the
        // user speaks the other language they use.
        NSLocale locale = string.IsNullOrWhiteSpace(_language)
            ? NSLocale.CurrentLocale
            : new NSLocale(_language);
        _recognizer = new SFSpeechRecognizer(locale)
            ?? throw new InvalidOperationException(
                $"This device has no speech recogniser for {(_language.Length > 0 ? _language : "the current language")}.");
        if (!_recognizer.Available)
            throw new InvalidOperationException("The speech recogniser is not available right now.");

        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.Record, AVAudioSessionCategoryOptions.DuckOthers);
        session.SetActive(true, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out NSError? sessionError);
        if (sessionError is not null)
            throw new InvalidOperationException("The microphone could not be started: " + sessionError.LocalizedDescription);

        _request = new SFSpeechAudioBufferRecognitionRequest
        {
            ShouldReportPartialResults = false,
            // Keep the audio on the device. See the class summary: a chat app that
            // runs its model locally must not ship the microphone to a server.
            RequiresOnDeviceRecognition = _recognizer.SupportsOnDeviceRecognition,
        };

        _completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        AVAudioFormat format = _engine.InputNode.GetBusOutputFormat(0);
        _engine.InputNode.InstallTapOnBus(0, 1024, format, (buffer, _) => _request?.Append(buffer));
        _engine.Prepare();
        _engine.StartAndReturnError(out NSError? engineError);
        if (engineError is not null)
            throw new InvalidOperationException("The microphone could not be started: " + engineError.LocalizedDescription);

        _task = _recognizer.GetRecognitionTask(_request, (result, error) =>
        {
            if (error is not null)
            {
                _completion?.TrySetException(new InvalidOperationException(error.LocalizedDescription));
                return;
            }
            if (result is { Final: true })
                _completion?.TrySetResult(result.BestTranscription?.FormattedString ?? string.Empty);
        });

        try
        {
            return await _completion.Task;
        }
        finally
        {
            Teardown();
        }
    }

    /// <summary>End the session; the recogniser then delivers its final transcription.</summary>
    public void Stop()
    {
        try
        {
            _engine.InputNode.RemoveTapOnBus(0);
            _engine.Stop();
            _request?.EndAudio();
        }
        catch (Exception)
        {
            // Stopping twice, or stopping a session that never started, is not worth
            // an error the user has to read.
        }
    }

    private void Teardown()
    {
        try
        {
            _engine.InputNode.RemoveTapOnBus(0);
            if (_engine.Running)
                _engine.Stop();
            _task?.Cancel();
            _task?.Dispose();
            _request?.Dispose();
            AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
        }
        catch (Exception) { /* teardown is best effort */ }
        finally
        {
            _task = null;
            _request = null;
        }
    }

    public void Dispose()
    {
        Teardown();
        _engine.Dispose();
        _recognizer?.Dispose();
    }
}
