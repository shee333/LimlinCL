using System;

namespace Limlin.Link
{
    /// <summary>当前登录的 Minecraft 账号信息，由启动器在启动联机时注入。</summary>
    public static class GameAccount
    {
        public sealed class Info
        {
            public string Name { get; set; }
            public string Uuid { get; set; }
            public bool IsPremium { get; set; }
        }

        public static Info Current { get; set; }
    }
}