#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YTray.Models
{
    internal sealed class ControlException : Exception
    {
        internal string Code { get; }
        internal string ExecutionState { get; }
        internal JObject? Verification { get; }
        internal ControlException(string code, string message, string executionState = "NotStarted", JObject? verification = null) : base(message)
        { Code = code; ExecutionState = executionState; Verification = verification; }
        internal JObject Result()
        {
            var error = new JObject {
                ["code"] = Code, ["execution_state"] = ExecutionState, ["message"] = Message,
                ["recovery"] = ExecutionState == "Unknown" ? "reobserve_do_not_replay" : "review_request"
            };
            if (Verification != null) error["verification"] = Verification.DeepClone();
            return new JObject {
                ["isError"] = true, ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = Message }),
                ["structuredContent"] = new JObject { ["error"] = error }
            };
        }
    }

    public sealed class ControlSessionOptions
    {
        public string ClientName { get; set; } = "本机 Agent";
        public string Origin { get; set; } = "";
        public string[] Executables { get; set; } = Array.Empty<string>();
        public bool ReadOnly { get; set; }
        public bool AllowForeground { get; set; } = true;
        public bool AllowDesktop { get; set; } = true;
        internal ControlSessionOptions Copy() => new ControlSessionOptions {
            ClientName = ClientName, Origin = Origin, Executables = Executables.ToArray(), ReadOnly = ReadOnly,
            AllowForeground = AllowForeground, AllowDesktop = AllowDesktop };
    }

    internal static class ControlJson
    {
        internal static string Secret() { using var rng = RandomNumberGenerator.Create(); var b = new byte[32]; rng.GetBytes(b); return Convert.ToBase64String(b); }
        internal static bool EqualSecret(string? supplied, string expected)
        {
            if (supplied == null || supplied.Length != expected.Length) return false;
            var difference = 0; for (var i = 0; i < supplied.Length; i++) difference |= supplied[i] ^ expected[i];
            return difference == 0;
        }
        internal static JObject Result(JObject value, string text = "") => new JObject {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text.Length == 0 ? value.ToString(Formatting.None) : text }),
            ["structuredContent"] = value, ["isError"] = false };
        internal static string Origin(string input)
        {
            input = input.Trim(); if (input.Length == 0) return "";
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
                || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Host.Contains("*")
                || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
                throw new ControlException("InvalidOrigin", "请输入精确 HTTPS 来源；本机开发来源可用 HTTP。来源不能带路径、账号或通配符。");
            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }
        // Deliberately bounded subset used by the reviewed Driver tools. Unknown schema
        // constructs are never a substitute for the explicit argument allowlists.
        internal static void Validate(JToken value, JObject schema, string path = "arguments")
        {
            void Reject() => throw new ControlException("InvalidArguments", path + " 不符合工具参数定义");
            if (schema["enum"] is JArray choices && !choices.Any(c => JToken.DeepEquals(c, value))) Reject();
            switch ((string?)schema["type"])
            {
                case "object":
                    if (!(value is JObject obj)) { Reject(); return; }
                    var properties = schema["properties"] as JObject ?? new JObject();
                    foreach (var key in (schema["required"] as JArray ?? new JArray()).Values<string>()) if (key != null && obj[key] == null) Reject();
                    foreach (var property in obj.Properties()) {
                        if (!(properties[property.Name] is JObject child)) { Reject(); continue; }
                        Validate(property.Value, child, path + "." + property.Name);
                    }
                    break;
                case "string": if (value.Type != JTokenType.String || value.Value<string>()!.Length > 20000) Reject(); break;
                case "boolean": if (value.Type != JTokenType.Boolean) Reject(); break;
                case "integer": case "number":
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float) { Reject(); break; }
                    var number = value.Value<double>();
                    if (double.IsNaN(number) || double.IsInfinity(number) || (string?)schema["type"] == "integer" && Math.Truncate(number) != number
                        || schema["minimum"] != null && number < schema["minimum"]!.Value<double>()
                        || schema["maximum"] != null && number > schema["maximum"]!.Value<double>()) Reject();
                    break;
                case "array":
                    if (!(value is JArray array) || array.Count > 20) { Reject(); break; }
                    if (schema["items"] is JObject item) foreach (var entry in array) Validate(entry, item, path + "[]");
                    break;
                default: Reject(); break;
            }
        }
    }
}
