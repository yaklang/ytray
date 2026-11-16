#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;
using YTray.Native;

namespace YTray.Core.SystemControl
{
    internal static class NativeControlWorker
    {
        internal static int Run()
        {
            var status = 0;
            var worker = new Thread(() => {
                try {
                    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
                    using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                    void Write(JObject message) { lock (writer) writer.WriteLine(message.ToString(Formatting.None)); }
                    void Notify(string method, JObject parameters) {
                        try { Write(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }); }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                    }
                    using var engine = new WindowsControlEngine(Notify);
                    string? line;
                    while ((line = reader.ReadLine()) != null) {
                        var request = JObject.Parse(line);
                        var id = request["id"];
                        var method = (string?)request["method"];
                        if (id == null && method != "feedback") continue;
                        var parameters = request["params"] as JObject ?? new JObject(); JObject result;
                        try {
                            switch (method) {
                                case "initialize": result = new JObject { ["protocolVersion"] = "2025-11-25", ["serverInfo"] = new JObject { ["name"] = "YTray native worker", ["version"] = YTrayBuildInfo.Version, ["build_id"] = YTrayBuildInfo.BuildId } }; break;
                                case "tools/list": result = new JObject { ["tools"] = NativeControlSchema.Catalog() }; break;
                                case "feedback": result = engine.ReportFeedback(parameters); break;
                                case "tray/inspect":
                                case "tray/restore": {
                                    using var cancellation = ControlCancellation.Open((string?)parameters["_cancel_id"]);
                                    var operationId = parameters.Value<long>("_operation_id");
                                    result = WindowsTrayControl.Run(parameters, method == "tray/restore", cancellation, step => {
                                        if (operationId != 0) Notify("operation/progress", new JObject { ["operation_id"] = operationId, ["step"] = step });
                                    });
                                    break;
                                }
                                case "inspection/highlight": {
                                    using var cancellation = ControlCancellation.Open((string?)parameters["_cancel_id"]);
                                    result = engine.HighlightInspection(parameters, cancellation);
                                    break;
                                }
                                case "read_context": {
                                    using var cancellation = ControlCancellation.Open((string?)parameters["_cancel_id"]);
                                    ControlCancellation.Check(cancellation);
                                    var window = new IntPtr(parameters.Value<long>("window_id"));
                                    System.Windows.Rect? region = null;
                                    if (parameters["region"] is JObject rectangle) region = new System.Windows.Rect(rectangle.Value<double>("x"), rectangle.Value<double>("y"), rectangle.Value<double>("w"), rectangle.Value<double>("h"));
                                    result = WindowsFocusContext.ReadAsync(window, region, (parameters["controls"] as JArray)?.OfType<JObject>().ToArray()).GetAwaiter().GetResult();
                                    ControlCancellation.Check(cancellation);
                                    break;
                                }
                                case "tools/call": {
                                    using var cancellation = ControlCancellation.Open((string?)parameters["_cancel_id"]);
                                    result = engine.Call((string?)parameters["name"] ?? "", parameters["arguments"] as JObject ?? new JObject(),
                                        cancellation, (long?)parameters["_operation_id"] ?? 0);
                                    break;
                                }
                                default: throw new ControlException("UnknownTool", "未知原生工作进程方法");
                            }
                        } catch (ControlException error) { result = error.Result(); }
                        catch (Exception error) { result = new ControlException("NativeOperationFailed", error.Message, NativeControlSchema.Actions.Contains((string?)parameters["name"] ?? "") ? "Unknown" : "NotStarted").Result(); }
                        if (id != null)
                            Write(new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
                    }
                } catch (Exception error) { try { Console.Error.WriteLine(error); } catch { } status = 1; }
            }) { IsBackground = false, Name = "YTray native automation" };
            worker.SetApartmentState(ApartmentState.MTA); worker.Start(); worker.Join(); return status;
        }
    }
}
