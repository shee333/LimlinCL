using System;
using System.Collections.Generic;

namespace Limlin.Link
{
    /// <summary>轻量文本解析：把 Campfire 的本地化 key 映射为中文，未收录时原样返回。</summary>
    internal static class Text
    {
        private static readonly Dictionary<string, string> Messages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Link.Error.DownloadUnreachable"] = "无法连接到联机组件下载源，请检查网络后重试。",
            ["Link.Error.DownloadFailed"] = "联机组件下载失败：{0}",
            ["Link.Error.ComponentMissing"] = "联机组件缺失，请重试。",
            ["Link.Error.HostNotFound"] = "没有找到房间，请确认邀请码是否正确，或让房主重新建房。",
            ["Link.Error.ConnectTimeout"] = "连接房间超时，请检查网络后重试。",
            ["Link.Error.RoomClosed"] = "房间已关闭。",
            ["Link.Error.VersionMismatch"] = "双方启动器版本不一致，请升级到相同版本。",
        };

        public static string Format(string key, params object[] args)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string template;
            if (!Messages.TryGetValue(key, out template)) template = key;
            try
            {
                return args == null || args.Length == 0 ? template : string.Format(template, args);
            }
            catch (FormatException)
            {
                return template;
            }
        }
    }
}