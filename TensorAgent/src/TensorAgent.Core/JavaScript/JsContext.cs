// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TensorAgent.Core.JavaScript;

/// <summary>A host function the engine installs into the JS global object.</summary>
/// <returns>The value the script sees; never <see cref="IntPtr.Zero"/> — use <see cref="JsContext.Undefined"/>.</returns>
internal delegate IntPtr JsHostFunction(JsContext context, IntPtr thisObject, IntPtr[] arguments);

/// <summary>
/// An error a host function raises, which the script sees as a JS <c>Error</c>.
/// <see cref="Code"/> and <see cref="Path"/> land on the error object as Node's
/// <c>err.code</c> and <c>err.path</c>, because Node code branches on them.
/// </summary>
internal class JsHostException : Exception
{
    public JsHostException(string message) : base(message) { }

    public string? Code { get; init; }

    public string? Path { get; init; }
}

/// <summary>
/// <c>process.exit(code)</c>. It unwinds the current JavaScript entry the only way
/// a host function can — by throwing — and the engine recognises it afterwards so
/// the run reports <see cref="ExitCode"/> instead of an uncaught error.
/// </summary>
internal sealed class ScriptExitException : Exception
{
    public ScriptExitException(int exitCode) : base($"process.exit({exitCode})") => ExitCode = exitCode;

    public int ExitCode { get; }
}

/// <summary>
/// Carries a JavaScript value back out through a managed unwind so it can be
/// re-thrown unchanged. <c>require</c> uses it: a module that throws a
/// <c>TypeError</c> must reach the caller as that <c>TypeError</c>, not as a
/// generic <c>Error</c> whose message happens to start with "TypeError:".
/// </summary>
internal sealed class JsRethrowException : Exception
{
    public JsRethrowException(IntPtr value, string message) : base(message) => Value = value;

    public IntPtr Value { get; }
}

/// <summary>A JavaScript exception that reached the host, already formatted for stderr.</summary>
internal sealed class JsScriptException : Exception
{
    public JsScriptException(JsErrorInfo info) : base(info.Summary) => Info = info;

    public JsErrorInfo Info { get; }
}

/// <summary>
/// A thrown JavaScript value, taken apart far enough to print it the way Node
/// prints an uncaught exception.
/// </summary>
/// <param name="Name">The constructor name (<c>TypeError</c>), or empty for a thrown non-object.</param>
/// <param name="Message">The <c>message</c> property, or the value's string form.</param>
/// <param name="SourceUrl">Where it was thrown, when the value carries it.</param>
/// <param name="Line">The 1-based line, or 0 when unknown.</param>
/// <param name="Frames">JavaScriptCore stack frames, already in Node's <c>at fn (file:line:col)</c> shape.</param>
/// <param name="Terminated">True when this is the watchdog stopping a runaway script rather than a real throw.</param>
/// <param name="ExitCode">Set when the throw is the one <c>process.exit</c> uses to unwind, and is its code.</param>
internal sealed record JsErrorInfo(
    string Name,
    string Message,
    string? SourceUrl,
    int Line,
    IReadOnlyList<string> Frames,
    bool Terminated,
    int? ExitCode = null)
{
    /// <summary>The one-line form, e.g. <c>TypeError: undefined is not a function</c>.</summary>
    public string Summary => Name.Length == 0 ? Message : $"{Name}: {Message}";

    /// <summary>The full report Node would print to stderr, newline terminated.</summary>
    public string ToStderr()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(SourceUrl) && Line > 0)
            sb.Append(SourceUrl).Append(':').Append(Line.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(Name.Length == 0 ? "Uncaught " + Message : Summary).Append('\n');
        foreach (string frame in Frames)
            sb.Append("    at ").Append(frame).Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// One JavaScriptCore VM plus the global context that runs in it, with the value
/// marshalling every global in this folder is written against.
///
/// <para>
/// A context group is a VM. Each run gets its own so that nothing — a global, a
/// pending promise, a leaked closure — survives from one script to the next; that
/// is the same isolation a child process would have given, and it is the reason
/// the engine can be handed to a model repeatedly without state bleeding across
/// tool calls.
/// </para>
/// </summary>
internal sealed unsafe class JsContext : IDisposable
{
    /// <summary>
    /// Host functions, keyed by the JS function object that fronts them.
    ///
    /// <para>
    /// <c>JSObjectMakeFunctionWithCallback</c> takes a bare C function pointer and
    /// no user data, so the callback cannot carry a closure. The identity of the
    /// function object it is called with is the only thing that distinguishes one
    /// host function from another, so it is the key. Entries are removed and their
    /// objects unprotected in <see cref="Dispose"/>, which is what keeps a pointer
    /// from being recycled underneath a live registration.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<IntPtr, Registration> s_registry = new();

    private sealed record Registration(JsContext Owner, JsHostFunction Handler);

    private readonly IntPtr _group;
    private readonly IntPtr _context;
    private readonly List<IntPtr> _protectedFunctions = new();
    private readonly List<IntPtr> _protectedValues = new();
    private bool _disposed;

    internal JsContext()
    {
        JsCore.EnsureInitialized();
        _group = JsCore.JSContextGroupCreate();
        if (_group == IntPtr.Zero)
            throw new InvalidOperationException("JSContextGroupCreate returned null");
        _context = JsCore.JSGlobalContextCreateInGroup(_group, IntPtr.Zero);
        if (_context == IntPtr.Zero)
        {
            JsCore.JSContextGroupRelease(_group);
            throw new InvalidOperationException("JSGlobalContextCreateInGroup returned null");
        }
        Global = JsCore.JSContextGetGlobalObject(_context);
    }

    internal IntPtr Global { get; }

    // ---- the watchdog ------------------------------------------------------------------

    /// <summary>
    /// Arms the VM to terminate any single entry into JavaScript that runs longer
    /// than <paramref name="seconds"/>.
    ///
    /// <para>
    /// The limit is measured from each entry into the VM, not from when it was
    /// set, and changing it from another thread does not affect an entry already
    /// running — verified, not assumed. So it must be re-armed with the REMAINING
    /// budget immediately before every evaluation and every timer callback, which
    /// is what <see cref="JavaScriptCoreEngine"/> does.
    /// </para>
    /// </summary>
    internal void ArmWatchdog(double seconds)
    {
        if (!JsCore.HasExecutionTimeLimit)
            return;
        JsCore.JSContextGroupSetExecutionTimeLimit(_group, Math.Max(0.0, seconds), IntPtr.Zero, IntPtr.Zero);
    }

    // ---- making values -----------------------------------------------------------------

    internal IntPtr Undefined => JsCore.JSValueMakeUndefined(_context);

    internal IntPtr Null => JsCore.JSValueMakeNull(_context);

    internal IntPtr Boolean(bool value) => JsCore.JSValueMakeBoolean(_context, value);

    internal IntPtr Number(double value) => JsCore.JSValueMakeNumber(_context, value);

    internal IntPtr String(string value)
    {
        using JsString text = JsString.Create(value);
        return JsCore.JSValueMakeString(_context, text.Handle);
    }

    internal IntPtr NewObject() => JsCore.JSObjectMake(_context, IntPtr.Zero, IntPtr.Zero);

    internal IntPtr NewArray(IReadOnlyList<IntPtr> items)
    {
        IntPtr[] buffer = items as IntPtr[] ?? items.ToArray();
        fixed (IntPtr* p = buffer)
        {
            IntPtr exception = IntPtr.Zero;
            IntPtr array = JsCore.JSObjectMakeArray(_context, (nuint)buffer.Length, p, &exception);
            ThrowIfException(exception);
            return array;
        }
    }

    internal IntPtr NewStringArray(IEnumerable<string> items)
        => NewArray(items.Select(String).ToArray());

    /// <summary>A <c>Uint8Array</c> holding a copy of <paramref name="bytes"/>.</summary>
    internal IntPtr NewUint8Array(ReadOnlySpan<byte> bytes)
    {
        IntPtr exception = IntPtr.Zero;
        IntPtr array = JsCore.JSObjectMakeTypedArray(_context, JsTypedArrayType.Uint8, (nuint)bytes.Length, &exception);
        ThrowIfException(exception);
        if (bytes.Length > 0)
        {
            void* destination = JsCore.JSObjectGetTypedArrayBytesPtr(_context, array, &exception);
            ThrowIfException(exception);
            bytes.CopyTo(new Span<byte>(destination, bytes.Length));
        }
        return array;
    }

    /// <summary>The bytes of a typed array, or null when <paramref name="value"/> is not one.</summary>
    internal byte[]? ToBytes(IntPtr value)
    {
        IntPtr exception = IntPtr.Zero;
        JsTypedArrayType type = JsCore.JSValueGetTypedArrayType(_context, value, &exception);
        if (exception != IntPtr.Zero || type is JsTypedArrayType.None or JsTypedArrayType.ArrayBuffer)
            return null;
        IntPtr obj = JsCore.JSValueToObject(_context, value, &exception);
        if (exception != IntPtr.Zero)
            return null;
        nuint length = JsCore.JSObjectGetTypedArrayLength(_context, obj, &exception);
        if (exception != IntPtr.Zero)
            return null;
        void* source = JsCore.JSObjectGetTypedArrayBytesPtr(_context, obj, &exception);
        if (exception != IntPtr.Zero || source == null)
            return length == 0 ? Array.Empty<byte>() : null;
        var bytes = new byte[checked((int)length)];
        new ReadOnlySpan<byte>(source, bytes.Length).CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Installs a host function and keeps it alive for the context's lifetime.
    /// The returned object is protected from collection because the registry keys
    /// on its address.
    /// </summary>
    internal IntPtr NewFunction(string name, JsHostFunction handler)
    {
        using JsString jsName = JsString.Create(name);
        IntPtr function = JsCore.JSObjectMakeFunctionWithCallback(_context, jsName.Handle, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nuint, IntPtr*, IntPtr*, IntPtr>)&Trampoline);
        if (function == IntPtr.Zero)
            throw new InvalidOperationException($"JSObjectMakeFunctionWithCallback failed for '{name}'");
        JsCore.JSValueProtect(_context, function);
        _protectedFunctions.Add(function);
        s_registry[function] = new Registration(this, handler);
        return function;
    }

    /// <summary>Defines <paramref name="name"/> on <paramref name="target"/> as a host function.</summary>
    internal void SetFunction(IntPtr target, string name, JsHostFunction handler)
        => SetProperty(target, name, NewFunction(name, handler));

    /// <summary>
    /// Roots a value against collection. Needed whenever a <c>JSValueRef</c> has to
    /// outlive the host call that received it — a pending timer's callback, a
    /// deferred promise's resolver — because the VM otherwise has no idea the host
    /// still holds it.
    /// </summary>
    internal void Protect(IntPtr value)
    {
        if (value != IntPtr.Zero)
            JsCore.JSValueProtect(_context, value);
    }

    internal void Unprotect(IntPtr value)
    {
        if (value != IntPtr.Zero && !_disposed)
            JsCore.JSValueUnprotect(_context, value);
    }

    /// <summary>
    /// Roots a value for the rest of the run. Used for the handful of things whose
    /// lifetime is the context's anyway — the module cache, an error object being
    /// carried back across a managed unwind — where tracking a release point would
    /// cost more than it saves.
    /// </summary>
    internal void ProtectUntilDispose(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return;
        Protect(value);
        _protectedValues.Add(value);
    }

    // ---- properties --------------------------------------------------------------------

    internal void SetProperty(IntPtr target, string name, IntPtr value)
    {
        using JsString key = JsString.Create(name);
        IntPtr exception = IntPtr.Zero;
        JsCore.JSObjectSetProperty(_context, target, key.Handle, value, 0, &exception);
        ThrowIfException(exception);
    }

    internal IntPtr GetProperty(IntPtr target, string name)
    {
        using JsString key = JsString.Create(name);
        IntPtr exception = IntPtr.Zero;
        IntPtr value = JsCore.JSObjectGetProperty(_context, target, key.Handle, &exception);
        ThrowIfException(exception);
        return value;
    }

    internal bool HasProperty(IntPtr target, string name)
    {
        using JsString key = JsString.Create(name);
        return JsCore.JSObjectHasProperty(_context, target, key.Handle);
    }

    /// <summary>The own, enumerable property names of an object, in JS order.</summary>
    internal IReadOnlyList<string> PropertyNames(IntPtr target)
    {
        using var names = new JsPropertyNames(_context, target);
        var result = new List<string>(names.Count);
        for (int i = 0; i < names.Count; i++)
            result.Add(names[i]);
        return result;
    }

    // ---- reading values ----------------------------------------------------------------

    internal JsType TypeOf(IntPtr value) => JsCore.JSValueGetType(_context, value);

    internal bool IsUndefined(IntPtr value) => value == IntPtr.Zero || TypeOf(value) == JsType.Undefined;

    internal bool IsNullish(IntPtr value) => value == IntPtr.Zero || TypeOf(value) is JsType.Undefined or JsType.Null;

    internal bool IsString(IntPtr value) => value != IntPtr.Zero && TypeOf(value) == JsType.String;

    internal bool IsObject(IntPtr value) => value != IntPtr.Zero && JsCore.JSValueIsObject(_context, value);

    internal bool IsArray(IntPtr value) => value != IntPtr.Zero && JsCore.JSValueIsArray(_context, value);

    internal bool IsFunction(IntPtr value) => IsObject(value) && JsCore.JSObjectIsFunction(_context, value);

    /// <summary>The <c>instanceof</c> test, used to tell a real <c>Error</c> from a plain object.</summary>
    internal bool IsInstanceOf(IntPtr value, IntPtr constructor)
    {
        if (!IsObject(value) || constructor == IntPtr.Zero)
            return false;
        IntPtr exception = IntPtr.Zero;
        bool result = JsCore.JSValueIsInstanceOfConstructor(_context, value, constructor, &exception);
        return exception == IntPtr.Zero && result;
    }

    /// <summary>A deferred promise plus its two resolvers, for host work that finishes off-thread.</summary>
    internal IntPtr NewPromise(out IntPtr resolve, out IntPtr reject)
    {
        IntPtr resolveLocal = IntPtr.Zero, rejectLocal = IntPtr.Zero, exception = IntPtr.Zero;
        IntPtr promise = JsCore.JSObjectMakeDeferredPromise(_context, &resolveLocal, &rejectLocal, &exception);
        ThrowIfException(exception);
        resolve = resolveLocal;
        reject = rejectLocal;
        return promise;
    }

    /// <summary>
    /// Defines a property the script can use but will not trip over: it does not
    /// show up in <c>Object.keys(globalThis)</c> and cannot be deleted, which is
    /// what keeps the engine's own plumbing out of a model's way.
    /// </summary>
    internal void SetHiddenProperty(IntPtr target, string name, IntPtr value)
    {
        const uint dontEnumDontDelete = 4 | 8;
        using JsString key = JsString.Create(name);
        IntPtr exception = IntPtr.Zero;
        JsCore.JSObjectSetProperty(_context, target, key.Handle, value, dontEnumDontDelete, &exception);
        ThrowIfException(exception);
    }

    internal bool ToBoolean(IntPtr value) => value != IntPtr.Zero && JsCore.JSValueToBoolean(_context, value);

    internal double ToNumber(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return double.NaN;
        IntPtr exception = IntPtr.Zero;
        double number = JsCore.JSValueToNumber(_context, value, &exception);
        ThrowIfException(exception);
        return number;
    }

    /// <summary>The value's string form, exactly as <c>String(value)</c> would produce it.</summary>
    internal string ToStringValue(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return "undefined";
        IntPtr exception = IntPtr.Zero;
        IntPtr copied = JsCore.JSValueToStringCopy(_context, value, &exception);
        if (exception != IntPtr.Zero || copied == IntPtr.Zero)
            return "[unstringifiable value]";
        using JsString text = JsString.Adopt(copied);
        return text.ToString();
    }

    /// <summary>
    /// <c>JSON.stringify(value, null, indent)</c>, or null when the value has no
    /// JSON form (undefined, a function, a cycle).
    /// </summary>
    internal string? ToJson(IntPtr value, uint indent = 0)
    {
        if (value == IntPtr.Zero)
            return null;
        IntPtr exception = IntPtr.Zero;
        IntPtr copied = JsCore.JSValueCreateJSONString(_context, value, indent, &exception);
        if (copied == IntPtr.Zero)
            return null;
        using JsString text = JsString.Adopt(copied);
        return text.ToString();
    }

    internal IntPtr FromJson(string json)
    {
        using JsString text = JsString.Create(json);
        return JsCore.JSValueMakeFromJSONString(_context, text.Handle);
    }

    // ---- calling and evaluating --------------------------------------------------------

    internal IntPtr Call(IntPtr function, IntPtr thisObject, params IntPtr[] arguments)
    {
        fixed (IntPtr* p = arguments)
        {
            IntPtr exception = IntPtr.Zero;
            IntPtr result = JsCore.JSObjectCallAsFunction(_context, function, thisObject, (nuint)arguments.Length, p, &exception);
            ThrowIfException(exception);
            return result;
        }
    }

    /// <summary>
    /// Calls a JavaScript function without letting a JS throw become a managed
    /// exception. The event loop needs this: a timer callback that throws is a
    /// program error to report, not a host failure to unwind through.
    /// </summary>
    internal IntPtr TryCall(IntPtr function, IntPtr thisObject, IntPtr[] arguments, out JsErrorInfo? error)
        => TryCall(function, thisObject, arguments, out error, out _);

    /// <summary>
    /// As above, but also hands back the thrown value itself so a caller that
    /// wants to re-throw the SAME error object — <c>require</c> propagating a
    /// module's failure — can, instead of flattening it to its message.
    /// </summary>
    internal IntPtr TryCall(IntPtr function, IntPtr thisObject, IntPtr[] arguments, out JsErrorInfo? error, out IntPtr thrown)
    {
        fixed (IntPtr* p = arguments)
        {
            IntPtr exception = IntPtr.Zero;
            IntPtr result = JsCore.JSObjectCallAsFunction(_context, function, thisObject, (nuint)arguments.Length, p, &exception);
            thrown = exception;
            error = exception == IntPtr.Zero ? null : Describe(exception);
            return result;
        }
    }

    /// <summary>
    /// Evaluates <paramref name="source"/>. A JavaScript exception is returned in
    /// <paramref name="error"/> rather than thrown, because the caller decides
    /// whether it is a script failure, a <c>process.exit</c>, or the watchdog.
    /// </summary>
    internal IntPtr Evaluate(string source, string? sourceUrl, int startingLineNumber, out JsErrorInfo? error)
    {
        using JsString script = JsString.Create(source);
        using JsString url = sourceUrl is null ? JsString.Adopt(IntPtr.Zero) : JsString.Create(sourceUrl);
        IntPtr exception = IntPtr.Zero;
        IntPtr result = JsCore.JSEvaluateScript(_context, script.Handle, IntPtr.Zero, url.Handle, startingLineNumber, &exception);
        error = exception == IntPtr.Zero ? null : Describe(exception);
        return result;
    }

    /// <summary>Compiles without running; the contract behind <c>node --check</c>.</summary>
    internal bool CheckSyntax(string source, string? sourceUrl, out JsErrorInfo? error)
    {
        using JsString script = JsString.Create(source);
        using JsString url = sourceUrl is null ? JsString.Adopt(IntPtr.Zero) : JsString.Create(sourceUrl);
        IntPtr exception = IntPtr.Zero;
        bool ok = JsCore.JSCheckScriptSyntax(_context, script.Handle, url.Handle, 1, &exception);
        error = exception == IntPtr.Zero ? null : Describe(exception);
        return ok;
    }

    // ---- errors ------------------------------------------------------------------------

    /// <summary>A JS <c>Error</c> carrying <paramref name="message"/>, plus Node's <c>code</c>/<c>path</c> when given.</summary>
    internal IntPtr MakeError(string message, string? code = null, string? path = null)
    {
        IntPtr messageValue = String(message);
        IntPtr exception = IntPtr.Zero;
        IntPtr error = JsCore.JSObjectMakeError(_context, 1, &messageValue, &exception);
        if (error == IntPtr.Zero)
            return String(message);
        if (code is not null)
            SetProperty(error, "code", String(code));
        if (path is not null)
            SetProperty(error, "path", String(path));
        return error;
    }

    /// <summary>Takes a thrown JS value apart into something printable.</summary>
    internal JsErrorInfo Describe(IntPtr value)
    {
        // Reading properties off the thrown value can itself throw — a Proxy with a
        // hostile getter is enough — and this runs on the path that reports a
        // failure, so it must not become one.
        try
        {
            return DescribeCore(value);
        }
        catch (Exception ex)
        {
            return new JsErrorInfo(string.Empty, "an exception was thrown that could not be inspected: " + ex.Message,
                null, 0, Array.Empty<string>(), false);
        }
    }

    private JsErrorInfo DescribeCore(IntPtr value)
    {
        if (!IsObject(value))
        {
            // The watchdog's termination arrives here, not as an Error object: it
            // is a bare value whose string form is the sentence below, with no
            // name, no stack and no properties to read.
            string text = ToStringValue(value);
            return new JsErrorInfo(string.Empty, text, null, 0, Array.Empty<string>(), IsTermination(text));
        }

        string name = StringPropertyOrEmpty(value, "name");
        string message = StringPropertyOrEmpty(value, "message");
        if (message.Length == 0 && name.Length == 0)
            message = ToStringValue(value);
        string? sourceUrl = NullableStringProperty(value, "sourceURL");
        int line = 0;
        IntPtr lineValue = GetProperty(value, "line");
        if (!IsNullish(lineValue))
            line = (int)ToNumber(lineValue);

        // The watchdog's termination is not a program error; it arrives as a plain
        // Error with this exact message and no stack, and the engine reports it as
        // a timeout instead of printing a traceback the model cannot act on.
        bool terminated = IsTermination(message);

        int? exitCode = null;
        IntPtr marker = GetProperty(value, ExitMarkerProperty);
        if (!IsNullish(marker))
            exitCode = (int)ToNumber(marker);

        return new JsErrorInfo(name, message, sourceUrl, line, StackFrames(value), terminated, exitCode);
    }

    /// <summary>What JavaScriptCore says when its execution watchdog stops a script.</summary>
    private static bool IsTermination(string message)
        => message.Contains("JavaScript execution terminated", StringComparison.Ordinal);

    /// <summary>
    /// JavaScriptCore writes stacks as <c>fn@file:line:col</c>; Node writes
    /// <c>at fn (file:line:col)</c>. A model has read a great many Node stacks and
    /// none in JavaScriptCore's dialect, so the frames are rewritten.
    /// </summary>
    private IReadOnlyList<string> StackFrames(IntPtr error)
    {
        string? stack = NullableStringProperty(error, "stack");
        if (string.IsNullOrEmpty(stack))
            return Array.Empty<string>();
        var frames = new List<string>();
        foreach (string raw in stack.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            int at = line.IndexOf('@');
            if (at < 0)
            {
                frames.Add(line);
                continue;
            }
            string function = line[..at];
            string location = line[(at + 1)..];
            if (function is "global code" or "")
                function = "<anonymous>";
            frames.Add(location.Length == 0 ? function : $"{function} ({location})");
        }
        return frames;
    }

    private string StringPropertyOrEmpty(IntPtr obj, string name) => NullableStringProperty(obj, name) ?? string.Empty;

    private string? NullableStringProperty(IntPtr obj, string name)
    {
        IntPtr value = GetProperty(obj, name);
        return IsNullish(value) ? null : ToStringValue(value);
    }

    /// <summary>Turns a pending JS exception into a managed one, for host code that cannot continue.</summary>
    private void ThrowIfException(IntPtr exception)
    {
        if (exception != IntPtr.Zero)
            throw new JsScriptException(Describe(exception));
    }

    // ---- the callback trampoline -------------------------------------------------------

    /// <summary>
    /// The single C entry point every host function is reached through.
    ///
    /// <para>
    /// It is <c>UnmanagedCallersOnly</c> and static, which is what makes it work
    /// under ahead-of-time compilation on iOS: there is no delegate to marshal and
    /// no reverse P/Invoke stub to generate at run time. The cost is that it can
    /// capture nothing, hence the registry lookup on the function object.
    /// </para>
    ///
    /// <para>
    /// No managed exception may cross this boundary — unwinding through JSC's
    /// frames would corrupt the VM — so everything is caught and turned into a
    /// JavaScript throw the script can see and handle.
    /// </para>
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr Trampoline(IntPtr context, IntPtr function, IntPtr thisObject, nuint argumentCount, IntPtr* arguments, IntPtr* exception)
    {
        if (!s_registry.TryGetValue(function, out Registration? registration))
            return JsCore.JSValueMakeUndefined(context);

        JsContext owner = registration.Owner;
        try
        {
            int count = checked((int)argumentCount);
            IntPtr[] args = count == 0 ? Array.Empty<IntPtr>() : new IntPtr[count];
            for (int i = 0; i < count; i++)
                args[i] = arguments[i];
            IntPtr result = registration.Handler(owner, thisObject, args);
            return result == IntPtr.Zero ? JsCore.JSValueMakeUndefined(context) : result;
        }
        catch (Exception ex)
        {
            if (exception != null)
                *exception = owner.ErrorValueFor(ex);
            return JsCore.JSValueMakeUndefined(context);
        }
    }

    /// <summary>Never throws: it runs on the unwind path out of a host function.</summary>
    private IntPtr ErrorValueFor(Exception ex)
    {
        try
        {
            if (ex is ScriptExitException exit)
            {
                IntPtr marker = MakeError(exit.Message, "ERR_PROCESS_EXIT");
                SetProperty(marker, ExitMarkerProperty, Number(exit.ExitCode));
                return marker;
            }
            if (ex is JsRethrowException rethrow)
                return rethrow.Value;
            if (ex is JsHostException host)
                return MakeError(host.Message, host.Code, host.Path);
            if (ex is JsScriptException script)
                return MakeError(script.Info.Summary);
            return MakeError(ex.Message);
        }
        catch
        {
            return Undefined;
        }
    }

    /// <summary>The property that marks the throw <c>process.exit</c> uses to unwind.</summary>
    internal const string ExitMarkerProperty = "__tensoragentExitCode";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (IntPtr function in _protectedFunctions)
        {
            s_registry.TryRemove(function, out _);
            JsCore.JSValueUnprotect(_context, function);
        }
        _protectedFunctions.Clear();
        foreach (IntPtr value in _protectedValues)
            JsCore.JSValueUnprotect(_context, value);
        _protectedValues.Clear();
        JsCore.JSGlobalContextRelease(_context);
        JsCore.JSContextGroupRelease(_group);
    }
}
