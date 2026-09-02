using System.Diagnostics;
using TensorAgent.Core.JavaScript;

namespace TensorAgent.Tests;

public sealed class TempProbe
{
    private const string LogPath = "/tmp/jsprobe2.log";

    [Fact]
    public void Dump()
    {
        File.WriteAllText(LogPath, "start\n");
        void Log(string s) { File.AppendAllText(LogPath, s + "\n"); }
        var guard = new Thread(() => { Thread.Sleep(40000); Log("HUNG - exiting"); Environment.Exit(3); }) { IsBackground = true };
        guard.Start();

        foreach (string code in new[] { "while(true){}", "let n=0; while(true){n++;}", "while(true){ Math.sqrt(2); }" })
        {
            using var js = new JsContext();
            js.ArmWatchdog(1.0);
            var sw = Stopwatch.StartNew();
            js.Evaluate(code, null, 1, out JsErrorInfo? e);
            sw.Stop();
            Log($"code='{code}' elapsed={sw.Elapsed.TotalSeconds:0.00}s summary={e?.Summary}");
        }
    }
}
