// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Shell;

/// <summary>Where a command's output goes: a capture, a file, a pipe buffer, or nowhere.</summary>
internal abstract class ShellWriter : IDisposable
{
    public abstract void Write(ReadOnlySpan<byte> bytes);

    public void Write(string text)
    {
        if (text.Length == 0)
            return;
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (max <= 4096)
        {
            Span<byte> buffer = stackalloc byte[max];
            int n = Encoding.UTF8.GetBytes(text, buffer);
            Write(buffer.Slice(0, n));
        }
        else
            Write(Encoding.UTF8.GetBytes(text));
    }

    public void WriteLine(string text)
    {
        Write(text);
        Write("\n");
    }

    public virtual void Flush() { }

    public virtual void Dispose() { }
}

internal sealed class CaptureWriter : ShellWriter
{
    private readonly OutputCapture _capture;

    public CaptureWriter(OutputCapture capture) => _capture = capture;

    public override void Write(ReadOnlySpan<byte> bytes) => _capture.Write(bytes);
}

internal sealed class StreamShellWriter : ShellWriter
{
    private readonly Stream _stream;
    private readonly bool _owns;

    public StreamShellWriter(Stream stream, bool owns)
    {
        _stream = stream;
        _owns = owns;
    }

    public Stream Stream => _stream;

    public override void Write(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public override void Flush() => _stream.Flush();

    public override void Dispose()
    {
        if (_owns)
            _stream.Dispose();
    }
}

internal sealed class NullShellWriter : ShellWriter
{
    public static readonly NullShellWriter Instance = new();

    public override void Write(ReadOnlySpan<byte> bytes) { }
}

/// <summary>A command's three standard streams.</summary>
internal sealed class ShellStreams
{
    public ShellStreams(Stream input, ShellWriter output, ShellWriter error)
    {
        In = input;
        Out = output;
        Err = error;
    }

    public Stream In { get; init; }
    public ShellWriter Out { get; init; }
    public ShellWriter Err { get; init; }

    public ShellStreams With(Stream? input = null, ShellWriter? output = null, ShellWriter? error = null)
        => new(input ?? In, output ?? Out, error ?? Err);

    /// <summary>The convention every utility follows: <c>name: message</c> on stderr.</summary>
    public void Error(string name, string message)
    {
        Err.Write(name);
        Err.Write(": ");
        Err.WriteLine(message);
    }
}

internal static class ShellText
{
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string ReadAllText(Stream stream)
    {
        if (stream == Stream.Null)
            return string.Empty;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Utf8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static byte[] ReadAllBytes(Stream stream)
    {
        if (stream == Stream.Null)
            return Array.Empty<byte>();
        if (stream is MemoryStream m && m.Position == 0)
        {
            byte[] all = m.ToArray();
            m.Position = m.Length;
            return all;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>One line without its newline, or null at end of input. Reads byte by byte so `read` in a loop does not eat the rest.</summary>
    public static string? ReadLine(Stream stream)
    {
        if (stream == Stream.Null)
            return null;
        var buffer = new List<byte>();
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
                return buffer.Count == 0 ? null : Utf8.GetString(buffer.ToArray());
            if (b == '\n')
                return Utf8.GetString(buffer.ToArray());
            buffer.Add((byte)b);
        }
    }

    /// <summary>Split into lines the way text utilities count them: a trailing newline does not add an empty line.</summary>
    public static List<string> Lines(string text)
    {
        var lines = new List<string>();
        if (text.Length == 0)
            return lines;
        int start = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                if (start < text.Length)
                    lines.Add(text.Substring(start));
                break;
            }
            lines.Add(text.Substring(start, nl - start));
            start = nl + 1;
        }
        return lines;
    }

    public static Stream FromString(string text) => new MemoryStream(Utf8.GetBytes(text));

    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        int n = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < n; i++)
        {
            if (bytes[i] == 0)
                return true;
        }
        return false;
    }

    public static string HumanSize(long bytes)
    {
        string[] units = { "B", "K", "M", "G", "T" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        if (unit == 0)
            return bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "B";
        return (value < 10 ? value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                           : Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)) + units[unit];
    }
}
