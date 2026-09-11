// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Globalization;
using System.Text;

namespace TensorAgent.Core.Sandbox;

/// <summary>
/// One output stream of a run: bounded, and tapped line by line while it grows.
///
/// <para>
/// The bound keeps both ends, as <c>ConfinedProcess.BoundedText</c> does for child
/// processes: a build's first hundred lines and its final error are what answer
/// "did it work", and the middle is what gets dropped. The tap fires for each
/// complete line as it is written, which is what the chat UI streams as tool
/// progress; the partial last line is delivered by <see cref="Complete"/>.
/// </para>
/// </summary>
public sealed class OutputCapture
{
    private readonly object _gate = new();
    private readonly StringBuilder _head = new();
    private readonly Queue<string> _tail = new();
    private readonly StringBuilder _pending = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly Action<string>? _onLine;
    private readonly int _limit;
    private int _headBytes;
    private int _tailBytes;
    private long _droppedBytes;
    private long _droppedLines;
    private bool _completed;

    public OutputCapture(int maxBytes, Action<string>? onLine = null)
    {
        _limit = Math.Max(2048, maxBytes);
        _onLine = onLine;
    }

    private int Half => _limit / 2;

    /// <summary>True once something was dropped, so a result can say so once.</summary>
    public bool Truncated
    {
        get { lock (_gate) return _droppedLines > 0; }
    }

    /// <summary>Total bytes written, dropped or not.</summary>
    public long TotalBytes { get; private set; }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        lock (_gate)
        {
            TotalBytes += Encoding.UTF8.GetByteCount(text);
            AppendChars(text);
        }
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        lock (_gate)
        {
            TotalBytes += bytes.Length;
            int chars = _decoder.GetCharCount(bytes, flush: false);
            if (chars == 0)
                return;
            char[] buffer = new char[chars];
            int written = _decoder.GetChars(bytes, buffer, flush: false);
            AppendChars(new string(buffer, 0, written));
        }
    }

    /// <summary>Deliver the partial last line to the tap. Safe to call more than once.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            if (_pending.Length > 0)
                _onLine?.Invoke(_pending.ToString());
        }
    }

    /// <summary>The captured text, with a note where the middle was dropped.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                var sb = new StringBuilder(_head.Length + _tailBytes + _pending.Length + 96);
                sb.Append(_head);
                if (_droppedLines > 0)
                {
                    sb.Append("\n… ").Append(_droppedLines.ToString(CultureInfo.InvariantCulture))
                      .Append(" lines (").Append(_droppedBytes.ToString(CultureInfo.InvariantCulture))
                      .Append(" bytes) of output were dropped from the middle …\n\n");
                }
                foreach (string line in _tail)
                    sb.Append(line).Append('\n');
                sb.Append(_pending);
                return sb.ToString();
            }
        }
    }

    private void AppendChars(string text)
    {
        int start = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                _pending.Append(text, start, text.Length - start);
                break;
            }
            _pending.Append(text, start, nl - start);
            string line = _pending.ToString();
            _pending.Clear();
            _onLine?.Invoke(line);
            Store(line);
            start = nl + 1;
        }

        // A run that never prints a newline still has to be bounded: a single
        // megabyte-long line is not a line anyone reads.
        if (_pending.Length > Half)
        {
            int keep = Half / 4;
            string cut = _pending.ToString();
            _droppedBytes += cut.Length - 2 * keep;
            _droppedLines++;
            _pending.Clear();
            _pending.Append(cut, 0, keep).Append(" …[line truncated]… ").Append(cut, cut.Length - keep, keep);
        }
    }

    private void Store(string line)
    {
        if (line.Length > Half)
        {
            _droppedBytes += line.Length - Half;
            _droppedLines++;
            line = line.Substring(0, Half / 2) + " …[line truncated]… " + line.Substring(line.Length - Half / 4);
        }

        int size = line.Length + 1;
        if (_tail.Count == 0 && _headBytes + size <= Half)
        {
            _head.Append(line).Append('\n');
            _headBytes += size;
            return;
        }

        _tail.Enqueue(line);
        _tailBytes += size;
        while (_tailBytes > Half && _tail.Count > 1)
        {
            string dropped = _tail.Dequeue();
            _tailBytes -= dropped.Length + 1;
            _droppedBytes += dropped.Length + 1;
            _droppedLines++;
        }
    }
}
