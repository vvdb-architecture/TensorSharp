// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using Microsoft.Extensions.Logging;

namespace TensorAgent.Core.Hosting;

/// <summary>
/// Every warning and error the app logs, written to a FILE with its stack trace.
///
/// <para>
/// The comment this replaces read "there is nowhere else for a phone to log to", and it
/// was true when it was written. It stopped being true the first time a failure had to be
/// diagnosed from a phone: <c>devicectl --console</c> detaches the moment the app is
/// backgrounded, which is exactly when the interesting failures happen, and a user
/// reporting "Object reference not set to an instance of an object" is reporting an
/// <see cref="Exception.Message"/> whose stack went to a console nobody was attached to.
/// </para>
/// <para>
/// So warnings and errors go to <c>logs/errors.log</c> as well, WITH the exception, and
/// come back off the device with <c>devicectl device copy from</c>. Information and below
/// are not written: this is for the lines that explain a failure, and a log that grows
/// with ordinary traffic is one nobody can find anything in.
/// </para>
/// </summary>
public sealed class DurableErrorLog : ILoggerProvider
{
    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Beyond this the file is started again. A phone the user cannot see the log on must not fill up.</summary>
    private const long MaxBytes = 1024 * 1024;

    public DurableErrorLog(string logsDirectory)
        => _path = Path.Combine(
            logsDirectory ?? throw new ArgumentNullException(nameof(logsDirectory)), "errors.log");

    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    public void Dispose()
    {
        // Nothing is held open: every line opens, appends and closes, because the
        // process this runs in is routinely killed outright by iOS and a buffered
        // writer's contents would be exactly the part worth reading.
    }

    private void Append(string category, LogLevel level, EventId id, string message, Exception? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (new FileInfo(_path) is { Exists: true, Length: > MaxBytes })
                File.Delete(_path);

            var line = new System.Text.StringBuilder();
            line.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ').Append(level)
                .Append(" [").Append(category).Append("] ")
                .Append(message);
            if (id.Id != 0)
                line.Append(" (event ").Append(id.Id).Append(')');
            line.AppendLine();
            if (error is not null)
                line.AppendLine(error.ToString());

            lock (_gate)
                File.AppendAllText(_path, line.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A diagnostic that cannot be written must never be the reason something
            // else fails.
        }
    }

    private sealed class Writer(DurableErrorLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            owner.Append(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
