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
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.JavaScript;

/// <summary>
/// The Node-shaped globals, built on top of a bare <see cref="JsContext"/>.
///
/// <para>
/// JavaScriptCore implements ECMAScript and stops there: it has no
/// <c>console</c> that goes anywhere useful, no <c>process</c>, no
/// <c>require</c>, no timers and no way to touch a file. Everything a script
/// written for Node expects is therefore a host function installed from here, and
/// every one of them is a deliberate decision about what this sandbox may do —
/// which is why they are C# rather than a bundled polyfill.
/// </para>
///
/// <para>
/// The surface is small on purpose. A model that asks for something absent gets a
/// clear error naming what it asked for, which is a better outcome than a stub
/// that returns an empty object and lets the script fail three steps later.
/// </para>
/// </summary>
internal sealed partial class NodeHost
{
    /// <summary>
    /// One client for the process. <c>fetch</c> is the only network egress a script
    /// has, and it is off entirely unless the policy says otherwise.
    /// </summary>
    private static readonly HttpClient s_http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        ConnectTimeout = TimeSpan.FromSeconds(20),
    })
    { Timeout = TimeSpan.FromSeconds(120) };

    private readonly JsContext _js;
    private readonly InterpreterContext _run;
    private readonly ExecutionPolicy _policy;
    private readonly ConfinedPaths _paths;
    private readonly OutputCapture _stdout;
    private readonly OutputCapture _stderr;
    private readonly JsEventLoop _loop;

    private IntPtr _shim;
    private IntPtr _errorConstructor;
    private IntPtr _bufferFrom;
    private IntPtr _processEnv;

    internal NodeHost(JsContext js, InterpreterContext run, ConfinedPaths paths, OutputCapture stdout, OutputCapture stderr, JsEventLoop loop)
    {
        _js = js;
        _run = run;
        _policy = run.Policy;
        _paths = paths;
        _stdout = stdout;
        _stderr = stderr;
        _loop = loop;
    }

    /// <summary>The working directory a script sees; there is no <c>chdir</c>, so it is fixed.</summary>
    private string Cwd => _run.WorkingDirectory;

    /// <summary>
    /// What <c>process.platform</c> and <c>os.platform()</c> report.
    ///
    /// <para>
    /// It is the constant "ios" even when the tests run on a Mac. The point of this
    /// engine is the iOS app, and a script that branches on the platform must take
    /// the same branch in a test as it does on a phone; a value that changed
    /// between the two would make the tests prove the wrong thing.
    /// </para>
    /// </summary>
    private const string PlatformName = "ios";

    // ---- installation ------------------------------------------------------------------

    /// <summary>Builds the global object a Node script expects. Throws if the shim cannot be evaluated.</summary>
    internal void Install(IReadOnlyList<string> argv)
    {
        _errorConstructor = _js.GetProperty(_js.Global, "Error");
        InstallByteHelpers();

        _js.Evaluate(NodeShim.Source, "<tensoragent-shim>", 1, out JsErrorInfo? shimError);
        if (shimError is not null)
            throw new InvalidOperationException("the JavaScript shim failed to load: " + shimError.Summary);
        _shim = _js.GetProperty(_js.Global, NodeShim.ShimObject);
        _bufferFrom = _js.GetProperty(_js.GetProperty(_js.Global, "Buffer"), "from");

        InstallConsole();
        InstallProcess(argv);
        InstallTimers();
        InstallFetch();
        InstallRequire();
    }

    /// <summary>
    /// The two byte conversions the shim cannot do for itself. Everything else in
    /// <c>Buffer</c> and <c>TextEncoder</c> is JavaScript.
    /// </summary>
    private void InstallByteHelpers()
    {
        IntPtr host = _js.NewObject();
        _js.SetFunction(host, "encode", (js, _, args) =>
            js.NewUint8Array(EncodeText(js.ToStringValue(At(args, 0)), EncodingName(js, At(args, 1)))));
        _js.SetFunction(host, "decode", (js, _, args) =>
            js.String(DecodeBytes(js.ToBytes(At(args, 0)) ?? Array.Empty<byte>(), EncodingName(js, At(args, 1)))));
        _js.SetHiddenProperty(_js.Global, NodeShim.HostObject, host);
    }

    // ---- console -----------------------------------------------------------------------

    private void InstallConsole()
    {
        IntPtr console = _js.NewObject();
        foreach (string name in new[] { "log", "info", "debug", "dir", "trace" })
            _js.SetFunction(console, name, (js, _, args) => WriteLine(js, args, _stdout));
        foreach (string name in new[] { "error", "warn" })
            _js.SetFunction(console, name, (js, _, args) => WriteLine(js, args, _stderr));
        _js.SetProperty(_js.Global, "console", console);
    }

    private IntPtr WriteLine(JsContext js, IntPtr[] args, OutputCapture sink)
    {
        var line = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
                line.Append(' ');
            line.Append(Format(args[i]));
        }
        line.Append('\n');
        sink.Write(line.ToString());
        return js.Undefined;
    }

    /// <summary>
    /// How one value prints.
    ///
    /// <para>
    /// Node runs values through <c>util.inspect</c>, which is a formatter of some
    /// size; this uses <c>JSON.stringify</c> for objects instead, so an object
    /// prints as <c>{"a":1}</c> where Node prints <c>{ a: 1 }</c>. The information
    /// is the same and the difference is visible, which is the trade worth making:
    /// a half-built inspect that gets nesting or cycles subtly wrong would be
    /// harder to trust than an obviously different one that never lies.
    /// </para>
    /// </summary>
    private string Format(IntPtr value)
    {
        switch (_js.TypeOf(value))
        {
            case JsType.Undefined:
                return "undefined";
            case JsType.Null:
                return "null";
            case JsType.String:
            case JsType.Boolean:
            case JsType.Number:
            case JsType.Symbol:
                return _js.ToStringValue(value);
            case JsType.BigInt:
                return _js.ToStringValue(value) + "n";
        }

        if (_js.IsFunction(value))
        {
            string name = _js.ToStringValue(_js.GetProperty(value, "name"));
            return name.Length == 0 ? "[Function (anonymous)]" : $"[Function: {name}]";
        }

        if (_js.IsInstanceOf(value, _errorConstructor))
        {
            JsErrorInfo info = _js.Describe(value);
            var sb = new StringBuilder(info.Summary);
            foreach (string frame in info.Frames)
                sb.Append("\n    at ").Append(frame);
            return sb.ToString();
        }

        return _js.ToJson(value) ?? _js.ToStringValue(value);
    }

    // ---- process -----------------------------------------------------------------------

    private void InstallProcess(IReadOnlyList<string> argv)
    {
        IntPtr process = _js.NewObject();
        _js.SetProperty(process, "argv", _js.NewStringArray(argv));
        _js.SetProperty(process, "argv0", _js.String("node"));
        _js.SetProperty(process, "platform", _js.String(PlatformName));
        _js.SetProperty(process, "arch", _js.String("arm64"));
        _js.SetProperty(process, "version", _js.String("v(JavaScriptCore)"));
        _js.SetProperty(process, "pid", _js.Number(Environment.ProcessId));

        _processEnv = _js.NewObject();
        foreach (KeyValuePair<string, string> entry in _run.Environment)
            _js.SetProperty(_processEnv, entry.Key, _js.String(entry.Value));
        _js.SetProperty(process, "env", _processEnv);

        _js.SetFunction(process, "cwd", (js, _, _) => js.String(Cwd));
        _js.SetFunction(process, "exit", (js, _, args) =>
        {
            int code = args.Length == 0 || js.IsNullish(args[0]) ? 0 : (int)js.ToNumber(args[0]);
            _loop.RequestExit(code);
            throw new ScriptExitException(code);
        });
        _js.SetFunction(process, "uptime", (js, _, _) => js.Number(_loop.ElapsedMs / 1000.0));
        _js.SetFunction(process, "nextTick", (js, _, args) =>
        {
            RequireFunction(js, At(args, 0), "process.nextTick");
            _loop.QueueMicrotask(args[0], args.Skip(1).ToArray());
            return js.Undefined;
        });

        _js.SetProperty(process, "stdout", MakeStream(_stdout));
        _js.SetProperty(process, "stderr", MakeStream(_stderr));

        // The shell hands standard input in as text; a script reaches it the way
        // Node scripts do when they are not streaming, which is one read.
        IntPtr stdin = _js.NewObject();
        _js.SetProperty(stdin, "isTTY", _js.Boolean(false));
        _js.SetFunction(stdin, "read", (js, _, _) =>
            string.IsNullOrEmpty(_run.StandardInput) ? js.Null : js.String(_run.StandardInput));
        _js.SetProperty(process, "stdin", stdin);

        _js.SetProperty(_js.Global, "process", process);
    }

    private IntPtr MakeStream(OutputCapture sink)
    {
        IntPtr stream = _js.NewObject();
        _js.SetProperty(stream, "isTTY", _js.Boolean(false));
        _js.SetFunction(stream, "write", (js, _, args) =>
        {
            if (args.Length > 0)
            {
                byte[]? bytes = js.ToBytes(args[0]);
                sink.Write(bytes is null ? js.ToStringValue(args[0]) : Encoding.UTF8.GetString(bytes));
            }
            return js.Boolean(true);
        });
        return stream;
    }

    /// <summary>The environment as the script left it; <c>process.env</c> is writable, as it is in Node.</summary>
    internal IReadOnlyDictionary<string, string> ReadEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_processEnv == IntPtr.Zero)
            return _run.Environment;
        foreach (string name in _js.PropertyNames(_processEnv))
        {
            IntPtr value = _js.GetProperty(_processEnv, name);
            if (!_js.IsNullish(value))
                result[name] = _js.ToStringValue(value);
        }
        return result;
    }

    // ---- timers ------------------------------------------------------------------------

    private void InstallTimers()
    {
        _js.SetFunction(_js.Global, "setTimeout", (js, _, args) => Schedule(js, args, repeating: false));
        _js.SetFunction(_js.Global, "setInterval", (js, _, args) => Schedule(js, args, repeating: true));
        _js.SetFunction(_js.Global, "setImmediate", (js, _, args) =>
        {
            RequireFunction(js, At(args, 0), "setImmediate");
            return js.Number(_loop.AddTimer(args[0], args.Skip(1).ToArray(), 0, repeating: false));
        });
        foreach (string name in new[] { "clearTimeout", "clearInterval", "clearImmediate" })
        {
            _js.SetFunction(_js.Global, name, (js, _, args) =>
            {
                if (args.Length > 0 && !js.IsNullish(args[0]))
                    _loop.ClearTimer((long)js.ToNumber(args[0]));
                return js.Undefined;
            });
        }
        _js.SetFunction(_js.Global, "queueMicrotask", (js, _, args) =>
        {
            RequireFunction(js, At(args, 0), "queueMicrotask");
            _loop.QueueMicrotask(args[0], Array.Empty<IntPtr>());
            return js.Undefined;
        });
    }

    private IntPtr Schedule(JsContext js, IntPtr[] args, bool repeating)
    {
        string name = repeating ? "setInterval" : "setTimeout";
        RequireFunction(js, At(args, 0), name);
        double delay = args.Length > 1 && !js.IsNullish(args[1]) ? js.ToNumber(args[1]) : 0;
        return js.Number(_loop.AddTimer(args[0], args.Skip(2).ToArray(), delay, repeating));
    }

    private void RequireFunction(JsContext js, IntPtr value, string caller)
    {
        if (!js.IsFunction(value))
            throw new JsHostException($"The \"callback\" argument to {caller} must be of type function") { Code = "ERR_INVALID_ARG_TYPE" };
    }

    // ---- fetch -------------------------------------------------------------------------

    /// <summary>
    /// <c>fetch</c>, defined either way.
    ///
    /// <para>
    /// When the network is off the function still exists and throws
    /// <see cref="ExecutionPolicy.NetworkDisabledMessage"/>. Leaving it undefined
    /// would be the lazier choice and a worse one: <c>fetch is not defined</c>
    /// reads like a missing polyfill and invites a model to go looking for one,
    /// while the sentence the user's setting produced explains itself and ends the
    /// attempt.
    /// </para>
    /// </summary>
    private void InstallFetch()
    {
        if (!_policy.AllowNetwork)
        {
            _js.SetFunction(_js.Global, "fetch", (_, _, _) => throw new JsHostException(ExecutionPolicy.NetworkDisabledMessage) { Code = "ENETDOWN" });
            return;
        }
        _js.SetFunction(_js.Global, "fetch", Fetch);
    }

    private IntPtr Fetch(JsContext js, IntPtr thisObject, IntPtr[] args)
    {
        string url = args.Length > 0 ? js.ToStringValue(args[0]) : string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new JsHostException($"Failed to parse URL from {url}") { Code = "ERR_INVALID_URL" };
        if (!_policy.IsHostAllowed(uri.Host))
            throw new JsHostException(ExecutionPolicy.HostNotAllowedMessage(uri.Host, _policy.NetworkHosts)) { Code = "ENETDOWN" };

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        IntPtr options = At(args, 1);
        if (js.IsObject(options))
        {
            IntPtr method = js.GetProperty(options, "method");
            if (!js.IsNullish(method))
                request.Method = new HttpMethod(js.ToStringValue(method).ToUpperInvariant());
            IntPtr body = js.GetProperty(options, "body");
            if (!js.IsNullish(body))
            {
                byte[]? bytes = js.ToBytes(body);
                request.Content = bytes is null
                    ? new StringContent(js.ToStringValue(body), Encoding.UTF8)
                    : new ByteArrayContent(bytes);
            }
            IntPtr headers = js.GetProperty(options, "headers");
            if (js.IsObject(headers))
            {
                foreach (string name in js.PropertyNames(headers))
                {
                    string value = js.ToStringValue(js.GetProperty(headers, name));
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                        request.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        IntPtr promise = js.NewPromise(out IntPtr resolve, out IntPtr reject);
        js.Protect(resolve);
        js.Protect(reject);
        _loop.StartJob();

        // The request runs off the JavaScript thread; only the settling runs back
        // on it, posted to the loop, because a JSValueRef may not be touched from
        // anywhere else.
        _ = Task.Run(async () =>
        {
            try
            {
                using HttpResponseMessage response = await s_http.SendAsync(request).ConfigureAwait(false);
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var headers = new List<KeyValuePair<string, string>>();
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    headers.Add(new KeyValuePair<string, string>(header.Key.ToLowerInvariant(), string.Join(", ", header.Value)));
                _loop.Post(() => Settle(js, resolve, reject, response.StatusCode, response.ReasonPhrase, uri, headers, bytes, null));
            }
            catch (Exception ex)
            {
                _loop.Post(() => Settle(js, resolve, reject, 0, null, uri, Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>(), ex));
            }
            finally
            {
                request.Dispose();
            }
        });

        return promise;
    }

    private void Settle(JsContext js, IntPtr resolve, IntPtr reject, System.Net.HttpStatusCode status, string? reason,
        Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, byte[] bytes, Exception? failure)
    {
        try
        {
            if (failure is not null)
            {
                js.TryCall(reject, js.Undefined, new[] { js.MakeError($"fetch failed: {failure.Message}", "ENETUNREACH") }, out _);
                return;
            }

            IntPtr raw = js.NewObject();
            js.SetProperty(raw, "status", js.Number((int)status));
            js.SetProperty(raw, "statusText", js.String(reason ?? string.Empty));
            js.SetProperty(raw, "ok", js.Boolean((int)status is >= 200 and < 300));
            js.SetProperty(raw, "url", js.String(uri.ToString()));
            js.SetProperty(raw, "body", js.String(Encoding.UTF8.GetString(bytes)));
            js.SetProperty(raw, "bytes", js.NewUint8Array(bytes));
            IntPtr headerObject = js.NewObject();
            foreach (KeyValuePair<string, string> header in headers)
                js.SetProperty(headerObject, header.Key, js.String(header.Value));
            js.SetProperty(raw, "headers", headerObject);

            IntPtr response = js.TryCall(js.GetProperty(_shim, "makeResponse"), _shim, new[] { raw }, out JsErrorInfo? error);
            if (error is not null)
                js.TryCall(reject, js.Undefined, new[] { js.MakeError(error.Summary) }, out _);
            else
                js.TryCall(resolve, js.Undefined, new[] { response }, out _);
        }
        finally
        {
            js.Unprotect(resolve);
            js.Unprotect(reject);
        }
    }

    // ---- text encodings ----------------------------------------------------------------

    /// <summary>The encoding named by an option, which Node accepts as a string or as <c>{ encoding }</c>.</summary>
    private string EncodingName(JsContext js, IntPtr options)
    {
        if (js.IsNullish(options))
            return "utf8";
        if (js.IsString(options))
            return js.ToStringValue(options);
        IntPtr encoding = js.GetProperty(options, "encoding");
        return js.IsNullish(encoding) ? "utf8" : js.ToStringValue(encoding);
    }

    private static byte[] EncodeText(string text, string encoding) => encoding.ToLowerInvariant() switch
    {
        "utf8" or "utf-8" or "" => Encoding.UTF8.GetBytes(text),
        "base64" => Convert.FromBase64String(Repad(text)),
        "hex" => FromHex(text),
        "latin1" or "binary" => Encoding.Latin1.GetBytes(text),
        "ascii" => Encoding.ASCII.GetBytes(text),
        "utf16le" or "utf-16le" or "ucs2" or "ucs-2" => Encoding.Unicode.GetBytes(text),
        _ => throw new JsHostException($"Unknown encoding: {encoding}") { Code = "ERR_UNKNOWN_ENCODING" },
    };

    private static string DecodeBytes(byte[] bytes, string encoding) => encoding.ToLowerInvariant() switch
    {
        "utf8" or "utf-8" or "" => Encoding.UTF8.GetString(bytes),
        "base64" => Convert.ToBase64String(bytes),
        "hex" => Convert.ToHexStringLower(bytes),
        "latin1" or "binary" => Encoding.Latin1.GetString(bytes),
        "ascii" => Encoding.ASCII.GetString(bytes),
        "utf16le" or "utf-16le" or "ucs2" or "ucs-2" => Encoding.Unicode.GetString(bytes),
        _ => throw new JsHostException($"Unknown encoding: {encoding}") { Code = "ERR_UNKNOWN_ENCODING" },
    };

    /// <summary>Node's base64 decoder ignores whitespace and missing padding; .NET's does not.</summary>
    private static string Repad(string text)
    {
        var cleaned = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '-' or '_')
                cleaned.Append(c is '-' ? '+' : c is '_' ? '/' : c);
        }
        while (cleaned.Length % 4 != 0)
            cleaned.Append('=');
        return cleaned.ToString();
    }

    private static byte[] FromHex(string text)
    {
        int length = text.Length - (text.Length % 2);
        var bytes = new byte[length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(text.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return bytes[..i];
        }
        return bytes;
    }

    // ---- argument helpers --------------------------------------------------------------

    private static IntPtr At(IntPtr[] args, int index) => index < args.Length ? args[index] : IntPtr.Zero;
}
