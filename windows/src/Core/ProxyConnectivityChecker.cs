#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YTray.Models;

namespace YTray.Core
{
    public class ProxyCheckResult : IEquatable<ProxyCheckResult>
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; } = "";
        public bool Equals(ProxyCheckResult? other) => other != null && IsSuccess == other.IsSuccess && Message == other.Message;
        public override bool Equals(object? obj) => Equals(obj as ProxyCheckResult);
        public override int GetHashCode() => (IsSuccess, Message).GetHashCode();
    }

    public class ProxyCheckDetail : IEquatable<ProxyCheckDetail>
    {
        public string Target { get; set; } = "";
        public bool IsSuccess { get; set; }
        public string Message { get; set; } = "";
        public int ElapsedMilliseconds { get; set; }
        public string Id => Target;
        public bool Equals(ProxyCheckDetail? other) => other != null && Target == other.Target;
        public override bool Equals(object? obj) => Equals(obj as ProxyCheckDetail);
        public override int GetHashCode() => Target.GetHashCode();
    }

    public class ProxyCheckReport : IEquatable<ProxyCheckReport>
    {
        public List<ProxyCheckDetail> Details { get; set; } = new List<ProxyCheckDetail>();
        public int SuccessCount => Details.Count(d => d.IsSuccess);
        public bool IsSuccess => SuccessCount > 0;
        public string Message => $"{(IsSuccess ? "检测成功" : "检测失败")} · {SuccessCount}/{Details.Count} 个目标可访问";
        public bool Equals(ProxyCheckReport? other) => other != null && Details.SequenceEqual(other.Details);
        public override bool Equals(object? obj) => Equals(obj as ProxyCheckReport);
        public override int GetHashCode() => Details.Count;
    }

    /// <summary>
    /// Mirrors macOS ProxyConnectivityChecker using raw TcpClient + CONNECT probe.
    /// </summary>
    public static class ProxyConnectivityChecker
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
        public static readonly string[] DefaultTargetStrings =
        {
            "https://example.com/", "https://baidu.com/", "https://google.com/",
        };

        public static Uri NormalizeTarget(string? raw)
        {
            var trimmed = (raw ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed)) throw new YTrayException(YTrayError.InvalidURL, raw);
            var candidate = trimmed.Contains("://") ? trimmed : "https://" + trimmed;
            var uri = new Uri(candidate);
            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https") throw new YTrayException(YTrayError.InvalidURL, raw);
            if (string.IsNullOrEmpty(uri.Host)) throw new YTrayException(YTrayError.InvalidURL, raw);
            return uri;
        }

        public static async Task<ProxyCheckReport> CheckDefaultTargetsAsync(ProxyEndpoint endpoint, string username, string password, string customTarget, TimeSpan timeout)
        {
            var targets = DefaultTargetStrings
                .Select(t => { try { return NormalizeTarget(t); } catch { return null; } })
                .OfType<Uri>()
                .ToList();
            var validationFailures = new List<ProxyCheckDetail>();
            var trimmedCustom = (customTarget ?? "").Trim();
            if (!string.IsNullOrEmpty(trimmedCustom))
            {
                try
                {
                    var cu = NormalizeTarget(trimmedCustom);
                    if (!targets.Any(t => string.Equals(t.AbsoluteUri, cu.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
                        targets.Add(cu);
                }
                catch (Exception ex)
                {
                    validationFailures.Add(new ProxyCheckDetail { Target = trimmedCustom, IsSuccess = false, Message = ex.Message, ElapsedMilliseconds = 0 });
                }
            }
            // Targets are independent. Sequential 10-second probes made the UI wait up to 30–40
            // seconds despite advertising a 10-second budget.
            var checks = targets.Select(async target =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var res = await CheckAsync(endpoint, username, password, target, timeout);
                sw.Stop();
                return new ProxyCheckDetail
                {
                    Target = target.AbsoluteUri,
                    IsSuccess = res.IsSuccess,
                    Message = res.Message,
                    ElapsedMilliseconds = (int)sw.ElapsedMilliseconds,
                };
            }).ToArray();
            var details = (await Task.WhenAll(checks)).ToList();
            return new ProxyCheckReport { Details = details.Concat(validationFailures).ToList() };
        }

        public static async Task<ProxyCheckResult> CheckAsync(ProxyEndpoint endpoint, string username, string password, Uri target, TimeSpan timeout)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(endpoint.Host, endpoint.Port);
                    var winner = await Task.WhenAny(connectTask, Task.Delay(timeout));
                    if (winner != connectTask || !client.Connected)
                    {
                        client.Close();
                        // ConnectAsync may complete after the timeout; observe its fault so it
                        // cannot surface later as an unobserved task exception.
                        CrashGuard.Observe(connectTask, "proxy-connect-timeout");
                        return new ProxyCheckResult { IsSuccess = false, Message = "检测超时 · 请检查 Host、端口或代理服务" };
                    }
                    await connectTask;

                    var usedCredentials = !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password);
                    var probe = ProbeRequest(target, username, password);
                    using (var stream = client.GetStream())
                    using (var cts = new CancellationTokenSource(timeout))
                    {
                        await stream.WriteAsync(probe, 0, probe.Length, cts.Token);
                        await stream.FlushAsync(cts.Token);

                        var buffer = new byte[8192];
                        int read;
                        try { read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token); }
                        catch { return new ProxyCheckResult { IsSuccess = false, Message = "读取代理响应超时" }; }
                        if (read == 0) return new ProxyCheckResult { IsSuccess = false, Message = "代理已连接，但没有返回检测响应" };
                        var data = new byte[read];
                        Array.Copy(buffer, data, read);
                        return InterpretResponse(data, usedCredentials);
                    }
                }
            }
            catch (Exception ex)
            {
                return new ProxyCheckResult { IsSuccess = false, Message = "连接失败 · " + ex.Message };
            }
        }

        public static ProxyCheckResult InterpretResponse(byte[] data, bool usedCredentials)
        {
            var response = Encoding.UTF8.GetString(data);
            var firstLine = response.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault() ?? "";
            var parts = firstLine.Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[1], out int status))
                return new ProxyCheckResult { IsSuccess = false, Message = "代理返回了无法识别的状态" };
            if (status >= 200 && status < 300)
                return new ProxyCheckResult { IsSuccess = true, Message = usedCredentials ? "检测成功 · 代理和认证可用" : "检测成功 · 代理可用" };
            if (status == 407)
                return new ProxyCheckResult { IsSuccess = false, Message = usedCredentials ? "认证失败 · 请检查用户名和密码" : "代理需要认证 · 请展开高级设置" };
            return new ProxyCheckResult { IsSuccess = false, Message = "代理响应异常 · HTTP " + status };
        }

        public static byte[] ProbeRequest(Uri target, string username, string password)
        {
            var host = target.Host;
            var port = target.IsDefaultPort ? (target.Scheme == "http" ? 80 : 443) : target.Port;
            var authority = $"{host}:{port}";
            var lines = new List<string> { $"CONNECT {authority} HTTP/1.1", $"Host: {authority}", "Proxy-Connection: close" };
            if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                lines.Add($"Proxy-Authorization: Basic {token}");
            }
            return Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n\r\n");
        }
    }
}
