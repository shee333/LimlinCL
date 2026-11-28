using System;
using Limlin.Link.Runtime;

namespace Limlin.Link
{
    /// <summary>供启动器调用的诊断与辅助入口。</summary>
    public static class LinkDiagnostics
    {
        /// <summary>生成一个新的邀请码。</summary>
        public static string NewInviteCode()
        {
            return RoomCode.Create().Code;
        }

        /// <summary>解析邀请码（可从整段聊天文本中提取），返回可读描述。</summary>
        public static string DescribeInvite(string text)
        {
            RoomCode code = RoomCode.Find(text);
            if (code == null) return "无效邀请码";
            return $"码={code.Code}；网络名={code.NetworkName}；密钥长度={code.NetworkSecret.Length}";
        }

        /// <summary>联机组件是否已下载。</summary>
        public static string ComponentState()
        {
            return EasyTierPackage.IsInstalled()
                ? $"已安装（EasyTier {EasyTierPackage.Version}，{EasyTierPackage.InstallDirectory}）"
                : "未下载";
        }

        /// <summary>生成一份示例 TOML，用于核对配置（房主，放行 25565）。</summary>
        public static string PreviewHostToml()
        {
            RoomCode code = RoomCode.Create();
            var config = new EasyTierConfig
            {
                NetworkName = code.NetworkName,
                NetworkSecret = code.NetworkSecret,
                Hostname = "limlin-host-25565",
                IPv4 = "10.144.144.1",
            };
            foreach (PublicNode node in PublicNodes.All)
                config.Peers.Add(node.Url);
            config.TcpWhitelist.Add("25565");
            config.UdpWhitelist.Add("25565");
            return config.ToToml();
        }

        /// <summary>返回内部版本信息。</summary>
        public static string Version()
        {
            return typeof(LinkDiagnostics).Assembly.GetName().Version.ToString(3)
                + " / EasyTier " + EasyTierPackage.Version;
        }
    }
}