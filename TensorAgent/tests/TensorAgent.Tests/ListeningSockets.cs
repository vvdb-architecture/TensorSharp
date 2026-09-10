using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Xunit.Sdk;

namespace TensorAgent.Tests;

/// <summary>
/// Does to a listening socket what iOS does to a suspended app's: closes it underneath
/// the managed <see cref="HttpListener"/> without telling the listener.
///
/// <para>
/// This is the only way to reproduce the failure off a device. The kernel's
/// pid_shutdown_sockets leaves the listener's object graph intact and its socket
/// dead, and the managed listener then shows nothing of it — IsListening stays true,
/// GetContextAsync never returns — while every connect is refused. Closing the
/// socket object by reflection leaves exactly that state behind: the
/// HttpEndPointListener is still registered for the port, its accept is gone, and
/// nothing has been logged. Dispose or Close on the listener itself would be a
/// different, tidier ending that the app never gets.
/// </para>
///
/// <para>
/// The lookup names runtime internals, so a runtime that renames them makes these
/// tests FAIL with the missing member named, never pass vacuously. The one legitimate
/// skip is a platform whose HttpListener is not the managed one, which is Windows
/// (http.sys); macOS, Linux and iOS all run the managed listener under test here.
/// </para>
/// </summary>
internal static class ListeningSockets
{
    /// <summary>True everywhere the app can run: only Windows has an http.sys-backed HttpListener.</summary>
    public static bool ManagedHttpListenerInUse => !OperatingSystem.IsWindows();

    public const string WhyNotManaged =
        "HttpListener is http.sys-backed on Windows; the reclaimed-socket scenario belongs to the managed listener (macOS, Linux, iOS)";

    /// <summary>Close the listening socket for <paramref name="port"/> out from under its HttpListener.</summary>
    public static void Kill(int port) => Find(port).Close();

    /// <summary>The socket the managed HttpListener is accepting on for <paramref name="port"/>.</summary>
    public static Socket Find(int port)
    {
        Assembly assembly = typeof(HttpListener).Assembly;
        Type manager = assembly.GetType("System.Net.HttpEndPointManager")
            ?? throw Missing("the type System.Net.HttpEndPointManager");
        FieldInfo endPointsField = manager.GetField("s_ipEndPoints", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw Missing("the static field System.Net.HttpEndPointManager.s_ipEndPoints");
        if (endPointsField.GetValue(null) is not IDictionary byAddress)
            throw Missing("System.Net.HttpEndPointManager.s_ipEndPoints as a Dictionary<IPAddress, Dictionary<int, HttpEndPointListener>>");

        object? endPointListener = null;
        var registered = new List<string>();
        // The runtime locks this same dictionary while it adds and removes listeners.
        lock (byAddress)
        {
            foreach (DictionaryEntry entry in byAddress)
            {
                if (entry.Value is not IDictionary byPort)
                    throw Missing("the values of System.Net.HttpEndPointManager.s_ipEndPoints as dictionaries keyed by port");
                foreach (object key in byPort.Keys)
                    registered.Add($"{entry.Key}:{key}");
                if (byPort.Contains(port))
                {
                    endPointListener = byPort[port];
                    break;
                }
            }
        }
        if (endPointListener is null)
        {
            throw new XunitException(
                $"no managed HttpListener end point is registered for port {port}; registered: [{string.Join(", ", registered)}]");
        }

        Type listenerType = endPointListener.GetType();
        FieldInfo socketField = listenerType.GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw Missing($"the instance field {listenerType.FullName}._socket");
        return socketField.GetValue(endPointListener) as Socket
            ?? throw Missing($"{listenerType.FullName}._socket holding a System.Net.Sockets.Socket");
    }

    private static XunitException Missing(string member) => new(
        $"the managed HttpListener in this runtime ({Environment.Version}) no longer has {member}; "
        + "ListeningSockets.Kill cannot reach the listening socket, so the reclaimed-socket tests need updating for it");
}
