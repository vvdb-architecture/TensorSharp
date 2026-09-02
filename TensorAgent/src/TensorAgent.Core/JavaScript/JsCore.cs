// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Reflection;
using System.Runtime.InteropServices;

namespace TensorAgent.Core.JavaScript;

/// <summary>What <c>JSValueGetType</c> reports; the order is the C header's.</summary>
internal enum JsType
{
    Undefined = 0,
    Null = 1,
    Boolean = 2,
    Number = 3,
    String = 4,
    Object = 5,
    Symbol = 6,
    BigInt = 7,
}

/// <summary>The <c>JSTypedArrayType</c> enumeration; only <see cref="Uint8"/> is used here.</summary>
internal enum JsTypedArrayType
{
    Int8 = 0,
    Int16 = 1,
    Int32 = 2,
    Uint8 = 3,
    Uint8Clamped = 4,
    Uint16 = 5,
    Uint32 = 6,
    Float32 = 7,
    Float64 = 8,
    ArrayBuffer = 9,
    None = 10,
    BigInt64 = 11,
    BigUint64 = 12,
}

/// <summary>
/// The slice of JavaScriptCore's <b>C</b> API this engine needs.
///
/// <para>
/// The C API is deliberate, not incidental. JavaScriptCore also ships an
/// Objective-C <c>JSContext</c> API, but that one needs the ObjC runtime bindings
/// and its blocks are hostile to ahead-of-time compilation, which is the only way
/// code runs on iOS. The C API is plain function calls over opaque pointers, so it
/// P/Invokes cleanly and AOTs without a bridge. It also has a second, larger
/// benefit: the framework lives at the same absolute path on macOS as on iOS, so
/// everything below runs — and is tested — on a developer's Mac rather than only
/// on a device.
/// </para>
///
/// <para>
/// Ownership follows the C API's own rule: anything named <c>Create</c> or
/// <c>Copy</c> is owned by the caller and must be released. Only two kinds of
/// object are returned that way — <c>JSStringRef</c> (see <see cref="JsString"/>)
/// and <c>JSPropertyNameArrayRef</c> — and both are wrapped so a caller cannot
/// hold one without a <c>using</c>. <c>JSValueRef</c> is garbage collected by the
/// VM instead, and only needs <c>JSValueProtect</c> when a value must outlive the
/// call that produced it (a pending timer callback, a deferred promise's resolver).
/// </para>
/// </summary>
internal static unsafe class JsCore
{
    /// <summary>
    /// The absolute framework path. It is identical on macOS, iOS, iPadOS and the
    /// simulator, which is why a single spelling serves every target.
    /// </summary>
    internal const string Library = "/System/Library/Frameworks/JavaScriptCore.framework/JavaScriptCore";

    private static readonly object s_gate = new();
    private static bool s_resolverRegistered;
    private static IntPtr s_moduleHandle;

    /// <summary>
    /// Registers the assembly's import resolver once, before any P/Invoke below.
    ///
    /// <para>
    /// The default runtime probe already opens the absolute path on macOS. The
    /// resolver exists for iOS, where the framework is linked into the app image
    /// and the app may be run from a location where <c>dlopen</c> of a system
    /// framework path is not what finds the symbols; there the main program handle
    /// does, exactly as <c>GgmlNative</c> resolves its statically linked archive.
    /// </para>
    /// </summary>
    internal static void EnsureResolver()
    {
        if (s_resolverRegistered)
            return;
        lock (s_gate)
        {
            if (s_resolverRegistered)
                return;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(JsCore).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Another type in this assembly got there first; its resolver
                // falls through to the default probe, which finds the framework.
            }
            s_resolverRegistered = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Library, StringComparison.Ordinal))
            return IntPtr.Zero;
        if (NativeLibrary.TryLoad(Library, out IntPtr handle))
            return handle;
        if (NativeLibrary.TryLoad("JavaScriptCore", assembly, searchPath, out handle))
            return handle;
        if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst())
            return NativeLibrary.GetMainProgramHandle();
        return IntPtr.Zero;
    }

    /// <summary>
    /// The loaded framework, or <see cref="IntPtr.Zero"/> when it is not present.
    /// Used only to ask whether an optional export exists — see
    /// <see cref="HasExecutionTimeLimit"/>.
    /// </summary>
    private static IntPtr ModuleHandle
    {
        get
        {
            if (s_moduleHandle != IntPtr.Zero)
                return s_moduleHandle;
            lock (s_gate)
            {
                if (s_moduleHandle == IntPtr.Zero && !NativeLibrary.TryLoad(Library, out s_moduleHandle))
                    s_moduleHandle = IntPtr.Zero;
                return s_moduleHandle;
            }
        }
    }

    private static bool? s_hasTimeLimit;

    /// <summary>
    /// Whether <c>JSContextGroupSetExecutionTimeLimit</c> is exported.
    ///
    /// <para>
    /// It is not in <c>JSContextRef.h</c>'s documented surface — it lives in
    /// <c>JSContextRefPrivate.h</c> — but it is exported from the shipping
    /// framework on every Apple platform, and it is the ONLY way to stop a
    /// runaway script. Probing the export rather than assuming it means a future
    /// OS that drops it degrades to the honest "still running" report instead of
    /// crashing on an <see cref="EntryPointNotFoundException"/>.
    /// </para>
    /// </summary>
    internal static bool HasExecutionTimeLimit
        => s_hasTimeLimit ??= ModuleHandle != IntPtr.Zero
            && NativeLibrary.TryGetExport(ModuleHandle, nameof(JSContextGroupSetExecutionTimeLimit), out _);

    // ---- context groups and contexts ---------------------------------------------------

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSContextGroupCreate();

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSContextGroupRelease(IntPtr group);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSGlobalContextCreateInGroup(IntPtr group, IntPtr globalObjectClass);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSGlobalContextRelease(IntPtr context);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSContextGetGlobalObject(IntPtr context);

    /// <summary>
    /// Arms the VM's watchdog: after <paramref name="limitSeconds"/> of CPU time in
    /// one entry into JavaScript, execution is terminated with a
    /// "JavaScript execution terminated." exception. A null
    /// <paramref name="callback"/> means "always terminate".
    /// </summary>
    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSContextGroupSetExecutionTimeLimit(IntPtr group, double limitSeconds, IntPtr callback, IntPtr userData);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSContextGroupClearExecutionTimeLimit(IntPtr group);

    // ---- evaluation --------------------------------------------------------------------

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSEvaluateScript(IntPtr context, IntPtr script, IntPtr thisObject, IntPtr sourceUrl, int startingLineNumber, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool JSCheckScriptSyntax(IntPtr context, IntPtr script, IntPtr sourceUrl, int startingLineNumber, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSGarbageCollect(IntPtr context);

    // ---- strings -----------------------------------------------------------------------

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSStringCreateWithCharacters(char* chars, nuint numChars);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSStringRelease(IntPtr value);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern nuint JSStringGetLength(IntPtr value);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern char* JSStringGetCharactersPtr(IntPtr value);

    // ---- values ------------------------------------------------------------------------

    [DllImport(Library, ExactSpelling = true)]
    internal static extern JsType JSValueGetType(IntPtr context, IntPtr value);

    [DllImport(Library, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool JSValueIsArray(IntPtr context, IntPtr value);

    [DllImport(Library, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool JSValueIsObject(IntPtr context, IntPtr value);

    [DllImport(Library, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool JSValueIsStrictEqual(IntPtr context, IntPtr a, IntPtr b);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSValueMakeUndefined(IntPtr context);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSValueMakeNull(IntPtr context);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSValueMakeBoolean(IntPtr context, [MarshalAs(UnmanagedType.U1)] bool value);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSValueMakeNumber(IntPtr context, double value);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSValueMakeString(IntPtr context, IntPtr value);

    [DllImport(Library, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool JSValueToBoolean(IntPtr context, IntPtr value);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern double JSValueToNumber(IntPtr context, IntPtr value, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSValueToStringCopy(IntPtr context, IntPtr value, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSValueToObject(IntPtr context, IntPtr value, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSValueCreateJSONString(IntPtr context, IntPtr value, uint indent, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSValueMakeFromJSONString(IntPtr context, IntPtr json);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSValueProtect(IntPtr context, IntPtr value);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSValueUnprotect(IntPtr context, IntPtr value);

    // ---- objects -----------------------------------------------------------------------

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectMake(IntPtr context, IntPtr jsClass, IntPtr data);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectMakeArray(IntPtr context, nuint argumentCount, IntPtr* arguments, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectMakeError(IntPtr context, nuint argumentCount, IntPtr* arguments, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectMakeFunctionWithCallback(IntPtr context, IntPtr name, IntPtr callback);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectMakeDeferredPromise(IntPtr context, IntPtr* resolve, IntPtr* reject, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectGetProperty(IntPtr context, IntPtr obj, IntPtr propertyName, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSObjectSetProperty(IntPtr context, IntPtr obj, IntPtr propertyName, IntPtr value, uint attributes, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool JSObjectHasProperty(IntPtr context, IntPtr obj, IntPtr propertyName);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectGetPropertyAtIndex(IntPtr context, IntPtr obj, uint index, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool JSObjectIsFunction(IntPtr context, IntPtr obj);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectCallAsFunction(IntPtr context, IntPtr obj, IntPtr thisObject, nuint argumentCount, IntPtr* arguments, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectCopyPropertyNames(IntPtr context, IntPtr obj);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern nuint JSPropertyNameArrayGetCount(IntPtr names);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSPropertyNameArrayGetNameAtIndex(IntPtr names, nuint index);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void JSPropertyNameArrayRelease(IntPtr names);

    // ---- typed arrays ------------------------------------------------------------------

    [DllImport(Library, ExactSpelling = true)]
    internal static extern IntPtr JSObjectMakeTypedArray(IntPtr context, JsTypedArrayType arrayType, nuint length, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern JsTypedArrayType JSValueGetTypedArrayType(IntPtr context, IntPtr value, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern void* JSObjectGetTypedArrayBytesPtr(IntPtr context, IntPtr obj, IntPtr* exception);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern nuint JSObjectGetTypedArrayLength(IntPtr context, IntPtr obj, IntPtr* exception);
}

/// <summary>
/// A <c>JSStringRef</c> that cannot outlive its scope.
///
/// <para>
/// Every <c>JSStringCreate*</c> and every <c>*Copy</c> hands back a retained
/// string that leaks unless <c>JSStringRelease</c> runs. Making this a
/// <c>ref struct</c> is the enforcement: the compiler refuses to put one in a
/// field, an array, a closure or a boxed object, so the only place it can live is
/// a local — and every local here is written as <c>using</c>. There is no way to
/// smuggle one past the end of a method, which is what "impossible to leak by
/// construction" has to mean to be worth saying.
/// </para>
/// </summary>
internal readonly ref struct JsString
{
    private readonly IntPtr _handle;

    private JsString(IntPtr handle) => _handle = handle;

    /// <summary>Wraps a string the C API just created and handed to us to own.</summary>
    internal static JsString Adopt(IntPtr owned) => new(owned);

    /// <summary>
    /// Copies <paramref name="text"/> into a new JS string. UTF-16 code units go
    /// across verbatim rather than through UTF-8, so lone surrogates and embedded
    /// NULs survive — <c>JSStringCreateWithUTF8CString</c> would truncate at the
    /// first NUL and re-encode the rest.
    /// </summary>
    internal static unsafe JsString Create(string text)
    {
        fixed (char* chars = text)
            return new JsString(JsCore.JSStringCreateWithCharacters(chars, (nuint)text.Length));
    }

    internal IntPtr Handle => _handle;

    internal bool IsNull => _handle == IntPtr.Zero;

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            JsCore.JSStringRelease(_handle);
    }

    public override unsafe string ToString()
    {
        if (_handle == IntPtr.Zero)
            return string.Empty;
        nuint length = JsCore.JSStringGetLength(_handle);
        if (length == 0)
            return string.Empty;
        char* chars = JsCore.JSStringGetCharactersPtr(_handle);
        return chars == null ? string.Empty : new string(chars, 0, checked((int)length));
    }
}

/// <summary>A <c>JSPropertyNameArrayRef</c>, released on scope exit for the same reason as <see cref="JsString"/>.</summary>
internal readonly ref struct JsPropertyNames
{
    private readonly IntPtr _handle;

    internal JsPropertyNames(IntPtr context, IntPtr obj) => _handle = JsCore.JSObjectCopyPropertyNames(context, obj);

    internal int Count => _handle == IntPtr.Zero ? 0 : checked((int)JsCore.JSPropertyNameArrayGetCount(_handle));

    /// <summary>The name at <paramref name="index"/>. The C API does not transfer ownership here, so nothing is released.</summary>
    internal string this[int index]
    {
        get
        {
            IntPtr name = JsCore.JSPropertyNameArrayGetNameAtIndex(_handle, (nuint)index);
            // Not adopted: JSPropertyNameArrayGetNameAtIndex borrows from the array.
            unsafe
            {
                nuint length = JsCore.JSStringGetLength(name);
                if (length == 0)
                    return string.Empty;
                char* chars = JsCore.JSStringGetCharactersPtr(name);
                return chars == null ? string.Empty : new string(chars, 0, checked((int)length));
            }
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            JsCore.JSPropertyNameArrayRelease(_handle);
    }
}
