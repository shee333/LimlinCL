# 联机模块（Limlin.Link）

## 1. 现状

| 项 | 状态 |
|---|---|
| 独立 C# 库 `Limlin.Link`（net48） | ✅ 编译通过，117 KB |
| 单文件打包 | ✅ 以嵌入资源形式进入 exe，`AssemblyResolve` 动态加载（产物仍是单 exe） |
| 邀请码生成/解析 | ✅ 自检通过（`L/XXXX-XXXX-XXXX-XXXX`，base34 + 校验，可从聊天文本中提取） |
| EasyTier TOML 生成 | ✅ `no_tun`、端口白名单、中转节点、压缩等参数正确 |
| 局域网端口发现 | ✅ 已移植（`GetExtendedTcpTable` + `0xFE` 探测） |
| 假局域网通告 | ✅ 已移植（`[MOTD]…[/MOTD][AD]端口[/AD]` → 127.0.0.1:4445） |
| 联机会话编排 `LinkSession` | ✅ 房主/客机两态：建房、找房主、端口转发、通告 |
| EasyTier 组件下载 | ✅ 已移植（官方 release + 固定 SHA-256 + 多镜像探测） |
| 房间服务（玩家列表/心跳/踢人） | ⏳ 下一步 |
| 自动开 LAN（Java Agent） | ⏳ 下一步 |
| 协议门禁（登录期拒绝/注入） | ⏳ 下一步 |
| Java Agent（自动开 LAN / Tab 信息） | ⏳ 下一步 |
| 联机页 UI 接线 | ✅ 已完成（创建/加入/取消、邀请码输入、房间信息卡） |

## 2. 代码结构

```
Limlin.Link/
├─ LinkSession.cs        房主/客机会话编排（对外主入口）
├─ LinkDiagnostics.cs    PCL 侧调用的诊断接口（邀请码、组件状态、TOML 预览）
├─ LinkSettings.cs       数据目录、玩家名、机器码、节点开关（PCL 注入）
├─ GameAccount.cs        当前账号信息（PCL 注入）
├─ RoomCode.cs           邀请码编解码（L/ 前缀，limlin-mc- 网络名）
├─ Text.cs / LinkException.cs / LinkLog.cs / Hex.cs / Json.cs / MultilineText.cs
├─ Room/                 帧协议、房间信息、玩家档案
├─ Runtime/              EasyTier 配置/实例/组件下载/子进程作业/远程 zip
└─ Minecraft/            端口发现、0xFE 探测、假局域网通告
```

## 3. 与 PCL 的接线点

| 位置 | 说明 |
|---|---|
| `Plain Craft Launcher 2.vbproj` | `ProjectReference` → `Limlin.Link.csproj`；`EmbeddedResource` 嵌入 DLL |
| `Application.xaml.vb` | `AssemblyResolve` 增加 `Limlin.Link` 前缀，从清单资源加载 |
| `Plain Craft Launcher 2\Modules\ModLink.vb` | VB 桥接层（可用性、邀请码、自检） |
| `FormMain.xaml.vb` | 存在 `PCL\link-selftest.on` 标记时运行自检 |
| `tools\build-release.ps1` | 首次构建自动 `dotnet restore` Limlin.Link |

## 4. 自检方法

```powershell
# 在启动器数据目录创建标记，然后启动启动器
echo 1 > "D:\MiencraftDEV\LimCL\product\PCL\link-selftest.on"
# 查看 PCL\Log1.txt 中的 [联机] 行，验证后删除标记
```

自检输出示例：

```
[联机] 库版本：1.0.0 / EasyTier 2.6.4
[联机] 邀请码生成：L/78CJ-D5LH-BEY1-0RVV
[联机] 邀请码解析：码=L/78CJ-D5LH-BEY1-0RVV；网络名=limlin-mc-78CJ-D5LH
[联机] 非法码解析：无效邀请码
[联机] TOML 预览：hostname = "limlin-host-25565" … no_tun = true …
```

## 5. 下一步计划

| 阶段 | 内容 |
|---|---|
| **P1 收尾** | 房间服务（`RoomServer/RoomClient`）：玩家列表、心跳、房间名/公告、白名单 |
| **UI 接线** | 重写 `PageLinkMain` 的 Loader 流程：创建房间/加入房间/邀请码展示/玩家列表；去掉对官方 `ServerConfig` 的依赖 |
| **自动开 LAN** | `-javaagent` 注入（启动器自己拼命令行，无需 attach），状态文件交给 `LanOpener` |
| **门禁** | `GameProxy` 明文阶段解析（登录期拒绝、按名踢人、欢迎语注入） |
| **打包** | EasyTier 组件按需下载（已具备），首次联机提示下载进度 |
## 6. UI 状态机（已实现）

```
待机 ──点「创建房间」──> 准备组件 ──> 等待「对局域网开放」──> （发现端口）──> 生成邀请码/显示房间卡
  │                                                                    │
  └──输入邀请码→「加入房间」──> 准备组件 ──> 寻找房主 ──> 端口转发 ──> 假 LAN 通告（出现在局域网世界）

任意阶段可点「取消」立即终止；关闭页面/关闭启动器会询问并结束会话。
```

**已验证**（单机实测）：

- 联机页渲染、创建房间进入等待、取消按钮与取消后状态恢复
- EasyTier 组件真实下载（约 33 MB，多镜像）+ 虚拟网络实例启动
- 邀请码生成/解析、TOML 生成
- 取消/关闭后无 easytier 残留进程（作业对象生效）

**待联调**：两台机器实际建房/进房（需要两名玩家与真实游戏）。