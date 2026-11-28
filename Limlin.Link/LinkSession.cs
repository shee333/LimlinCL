using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Limlin.Link.Minecraft;
using Limlin.Link.Runtime;

namespace Limlin.Link
{
    public enum LinkRole
    {
        Host,
        Guest,
    }

    /// <summary>
    /// 一次联机会话：创建房间或加入房间。
    /// <para>
    /// 房主：发现局域网端口 → 生成邀请码 → 启动虚拟网络（只放行该端口）→ 等待节点连通。
    /// 客机：解析邀请码 → 启动虚拟网络（不放行任何入向）→ 找到房主 → 本地端口转发 → 广播假局域网世界。
    /// </para>
    /// </summary>
    public sealed class LinkSession : IDisposable
    {
        /// <summary>房主 hostname 前缀，后面紧跟游戏端口，供客机零配置发现。</summary>
        private const string HostTag = "limlin-host-";
        private const string GuestTag = "limlin-guest-";
        /// <summary>房主在虚拟网络中的固定地址。</summary>
        private const string HostVirtualIp = "10.144.144.1";
        /// <summary>客机广播到本机局域网列表时的文字。</summary>
        private const string GuestMotd = "栎木栎林社区 · 好友房间";

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private EasyTierInstance _tier;
        private LanWorldAnnouncer _announcer;
        private volatile bool _running;

        public LinkRole Role { get; private set; }
        public string InviteCode { get; private set; }
        public int GamePort { get; private set; }
        public int LocalPort { get; private set; }
        public bool IsRunning => _running;

        public event Action<string> Progress;
        public event Action<string> InviteCodeReady;
        public event Action<int> LocalWorldReady;
        public event Action<string> Failed;

        private LinkSession(LinkRole role)
        {
            Role = role;
        }

        /// <summary>创建房间。<paramref name="lanPort"/> 为 0 时自动等待游戏中「对局域网开放」。</summary>
        public static LinkSession StartHost(int lanPort = 0, Action<string> progress = null)
        {
            var session = new LinkSession(LinkRole.Host);
            session.Progress = progress;
            session.StartBackground(async (ct) => await session.RunHostAsync(lanPort, ct));
            return session;
        }

        /// <summary>加入房间。</summary>
        public static LinkSession Join(string inviteCode, Action<string> progress = null)
        {
            var session = new LinkSession(LinkRole.Guest);
            session.Progress = progress;
            session.StartBackground(async (ct) => await session.RunGuestAsync(inviteCode, ct));
            return session;
        }

        private void StartBackground(Func<CancellationToken, Task> work)
        {
            Task.Run(async () =>
            {
                try
                {
                    await work(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Report("联机已取消");
                }
                catch (Exception ex)
                {
                    LinkLog.Warn(ex.ToString());
                    Fail(ex.Message);
                }
            });
        }

        private async Task RunHostAsync(int lanPort, CancellationToken ct)
        {
            Report("正在准备联机组件…");
            await EnsureComponentAsync(ct).ConfigureAwait(false);

            int port = lanPort;
            if (port <= 0)
            {
                Report("请在游戏中打开「对局域网开放」，正在等待…");
                port = await LocalWorldFinder.WaitForLocalWorldAsync(ct).ConfigureAwait(false);
            }
            if (port <= 0) throw new InvalidOperationException("未发现可用的局域网端口");
            GamePort = port;
            Report($"已发现局域网端口 {port}");

            RoomCode code = RoomCode.Create();
            InviteCode = code.Code;
            var config = new EasyTierConfig
            {
                NetworkName = code.NetworkName,
                NetworkSecret = code.NetworkSecret,
                Hostname = HostTag + port,
                IPv4 = HostVirtualIp,
            };
            AddNodes(config);
            config.TcpWhitelist.Add(port.ToString());
            config.UdpWhitelist.Add(port.ToString());

            _tier = EasyTierInstance.Start(config);
            Report("正在连接中转节点…");
            await WaitForNetworkAsync(ct).ConfigureAwait(false);

            _running = true;
            InviteCodeReady?.Invoke(InviteCode);
            Report($"房间已创建，邀请码：{InviteCode}");
        }

        private async Task RunGuestAsync(string inviteCode, CancellationToken ct)
        {
            RoomCode code = RoomCode.Find(inviteCode);
            if (code == null) throw new InvalidOperationException("邀请码无效，请检查后重试");

            Report("正在准备联机组件…");
            await EnsureComponentAsync(ct).ConfigureAwait(false);

            var config = new EasyTierConfig
            {
                NetworkName = code.NetworkName,
                NetworkSecret = code.NetworkSecret,
                Hostname = GuestTag + Hex.Random(3),
            };
            AddNodes(config);
            config.TcpWhitelist.Add("0");

            _tier = EasyTierInstance.Start(config);
            Report("正在寻找房主…");
            Peer host = await WaitForHostAsync(ct).ConfigureAwait(false);
            GamePort = host.Port;
            Report($"已找到房主（延迟 {host.LatencyMs?.ToString("0") ?? "?"} ms），正在建立本地转发…");

            LocalPort = FindFreePort();
            bool forwarded = await _tier.AddPortForwardAsync(
                "tcp",
                new IPEndPoint(IPAddress.Loopback, LocalPort),
                new IPEndPoint(IPAddress.Parse(host.IPv4), host.Port),
                ct).ConfigureAwait(false);
            if (!forwarded) throw new InvalidOperationException("端口转发失败，可能被安全软件拦截");

            _announcer = new LanWorldAnnouncer(() => GuestMotd, LocalPort, true);
            _running = true;
            LocalWorldReady?.Invoke(LocalPort);
            Report("已加入房间，请在「多人游戏 → 局域网世界」中进入");
        }

        private async Task EnsureComponentAsync(CancellationToken ct)
        {
            if (EasyTierPackage.IsInstalled())
            {
                Report("联机组件已就绪");
                return;
            }
            Report("正在下载联机组件（首次使用需要）…");
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Total > 0) Report($"正在下载联机组件 {p.Fraction:P0}");
            });
            await EasyTierPackage.InstallAsync(progress, ct).ConfigureAwait(false);
            Report("联机组件已就绪");
        }

        private static void AddNodes(EasyTierConfig config)
        {
            foreach (PublicNode node in PublicNodes.All.Where(LinkSettings.IsNodeEnabled))
                config.Peers.Add(node.Url);
            foreach (string url in LinkSettings.CustomNodeUrls)
                if (!string.IsNullOrWhiteSpace(url)) config.Peers.Add(url);
        }

        private async Task WaitForNetworkAsync(CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                int connected = await _tier.CountConnectedNodesAsync(ct).ConfigureAwait(false);
                if (connected > 0)
                {
                    Report($"已连接 {connected} 个中转节点");
                    return;
                }
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
            Report("中转节点连接超时，仍然尝试直连");
        }

        private sealed class Peer
        {
            public string IPv4;
            public int Port;
            public double? LatencyMs;
        }

        private async Task<Peer> WaitForHostAsync(CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var peers = await _tier.GetPeersAsync(ct).ConfigureAwait(false);
                foreach (EasyTierPeer peer in peers)
                {
                    if (peer.IsLocal || string.IsNullOrEmpty(peer.IPv4)) continue;
                    if (peer.Hostname == null || !peer.Hostname.StartsWith(HostTag, StringComparison.Ordinal)) continue;
                    int port;
                    if (!int.TryParse(peer.Hostname.Substring(HostTag.Length), out port) || port <= 0 || port > 65535)
                        continue;
                    return new Peer { IPv4 = peer.IPv4, Port = port, LatencyMs = peer.LatencyMs };
                }
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            throw new TimeoutException("没有找到房间，请确认房主已创建房间且邀请码正确");
        }

        private static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private void Report(string text)
        {
            try { Progress?.Invoke(text); } catch { }
            LinkLog.Info(text);
        }

        private void Fail(string message)
        {
            try { Failed?.Invoke(message); } catch { }
        }

        public void Stop()
        {
            _running = false;
            try { _cts.Cancel(); } catch { }
            try { _announcer?.Dispose(); } catch { }
            _announcer = null;
            try { _tier?.Dispose(); } catch { }
            _tier = null;
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }
}