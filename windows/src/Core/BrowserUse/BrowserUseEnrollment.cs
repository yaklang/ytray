#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Core.BrowserUse
{
    internal sealed class BrowserUseEnrollment
    {
        private readonly Lazy<byte[]> _key;

        internal BrowserUseEnrollment(string applicationDirectory)
        {
            var store = new ControlAccessStore(applicationDirectory, "BrowserUse", "managed-instances.bin",
                "浏览器实例接入配置已损坏。");
            _key = new Lazy<byte[]>(() => Convert.FromBase64String(store.Open(restore: true)));
        }

        internal string Issue(Guid instance)
        {
            using var signer = new HMACSHA256(_key.Value);
            return Convert.ToBase64String(signer.ComputeHash(Encoding.ASCII.GetBytes("YTray managed browser " + instance.ToString("D"))));
        }

        internal bool Accepts(Guid instance, string? ticket) => ticket?.Length == 44
            && ControlJson.EqualSecret(ticket, Issue(instance));
    }
}
