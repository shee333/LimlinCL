using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Limlin.Link
{
    /// <summary>
    /// 启动器侧的联机配置与路径。由 PCL 在启动时注入实际值（玩家名、实例信息等），
    /// 未注入时使用安全的本地默认值。
    /// </summary>
    public static class LinkSettings
    {
        private static string _dataDirectory;

        /// <summary>联机数据目录（EasyTier 运行时、日志、状态文件）。</summary>
        public static string DataDirectory
        {
            get
            {
                if (_dataDirectory != null) return _dataDirectory;
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _dataDirectory = Path.Combine(root, "Limlin", "Link");
                return _dataDirectory;
            }
            set { _dataDirectory = value; }
        }

        /// <summary>玩家显示名。</summary>
        public static string EffectivePlayerName { get; set; } = "玩家";

        /// <summary>本机稳定标识（用于房间成员识别）。</summary>
        public static string MachineId
        {
            get
            {
                if (_machineId != null) return _machineId;
                try
                {
                    string raw = Environment.MachineName + "|" + Environment.UserName;
                    using (var sha = SHA256.Create())
                    {
                        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                        _machineId = BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
                    }
                }
                catch
                {
                    _machineId = "unknown";
                }
                return _machineId;
            }
        }
        private static string _machineId;

        /// <summary>是否展示玩家皮肤。</summary>
        public static bool ShowSkins { get; set; } = true;

        /// <summary>是否共享客户端版本信息。</summary>
        public static bool ShareVersion { get; set; } = true;

        /// <summary>自定义节点列表（启动器配置后注入）。</summary>
        internal static List<string> CustomNodeUrls { get; } = new List<string>();

        /// <summary>被禁用的内置节点 ID。</summary>
        internal static HashSet<string> DisabledNodeIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>节点是否启用（PublicNodes 以方法组形式使用）。</summary>
        internal static bool IsNodeEnabled(PublicNode node)
        {
            return node != null && !DisabledNodeIds.Contains(node.Id);
        }

        /// <summary>网络节点是否被禁用（按 URL）。</summary>
        internal static bool IsUrlEnabled(string url)
        {
            return !string.IsNullOrWhiteSpace(url);
        }
    }
}