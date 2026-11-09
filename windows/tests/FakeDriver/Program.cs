using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static readonly object Gate = new object();
    private static void Reply(JToken id, JObject result) { lock (Gate) { Console.WriteLine(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToString(Formatting.None)); Console.Out.Flush(); } }
    private static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--sleep") >= 0) { Thread.Sleep(Timeout.Infinite); return; }
        if (args.Length == 2 && args[0] == "--job-host") {
            var environment = new System.Collections.Generic.Dictionary<string, string>();
            foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables()) environment[(string)pair.Key] = (string)pair.Value;
            var type = System.Reflection.Assembly.LoadFrom(args[1]).GetType("YTray.Native.DriverProcessLauncher");
            var owner = type.GetMethod("Start", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { Process.GetCurrentProcess().MainModule.FileName, environment, "--sleep" });
            var process = (Process)type.GetProperty("Process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(owner);
            Console.WriteLine(process.Id); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); GC.KeepAlive(owner); return;
        }
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        string line;
        while ((line = Console.ReadLine()) != null) {
            var message = JObject.Parse(line); var id = message["id"]; if (id == null) continue;
            var method = (string)message["method"]; var parameters = message["params"] as JObject ?? new JObject();
            switch (method) {
                case "initialize": Reply(id, new JObject { ["protocolVersion"] = "2025-11-25" }); break;
                case "tools/list": Reply(id, new JObject { ["tools"] = new JArray() }); break;
                case "test/echo": _ = Task.Run(async () => { await Task.Delay((int?)parameters["delay"] ?? 0); Reply(id, parameters); }); break;
                case "test/hang": break;
                case "test/badframe": Console.WriteLine("not json"); Console.Out.Flush(); break;
                case "test/stderr": Console.Error.Write(new string('x', 1000000)); Console.Error.Flush(); Reply(id, new JObject { ["ok"] = true }); break;
                case "test/child": var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--sleep") { UseShellExecute = false, CreateNoWindow = true }); Reply(id, new JObject { ["pid"] = child.Id }); break;
            }
        }
    }
}
