# PCL 整合包（.mrpack）安装链路详解

> 本文基于 `Meloong-Git/PCL` 源码（本仓库 `dev` 分支）逐行追踪，用于后续实现「社区整合包一键安装」等大功能前的地基调研。
> 所有行号对应当前仓库状态，代码变动后可能漂移，请以函数名为准。

---

## 0. 一句话总结

玩家把 `.mrpack` 拖进 PCL 后，PCL 会：

**读取 `modrinth.index.json` → 解析依赖（MC 版本 + 加载器）→ 解压 overrides → 把每个文件展开成「官方 CDN + 镜像 CDN」候选 URL 列表 → 交给多线程下载引擎（失败自动换源）→ 同时并行安装原版与加载器 → 合并 JSON、搬文件、收尾**。

其中「第三方镜像」分两条完全独立的线：

| 资源类型 | 镜像实现 | 位置 |
|---|---|---|
| **Mod / 整合包文件**（Modrinth、CurseForge CDN） | 域名重写到 `mod.mcimirror.top`（MCIM） | [ModDownload.vb:1304](#41-mod-文件镜像mcim) |
| **原版 / 加载器 / 资源文件**（Mojang） | 域名重写到 `bmclapi2.bangbang93.com` | [ModDownload.vb:1248-1301](#42-原版与加载器镜像bmclapi) |

---

## 1. 入口层：拖拽

```
用户拖入文件
  └─ FormMain.xaml.vb:953  FrmMain_Drop           ← WPF DragEventArgs 处理
       └─ FormMain.xaml.vb:1031  FileDrag(路径列表) ← 后台线程里分发
            ├─ 扩展名判定（:1076）
            │    {"zip", "rar", "mrpack"} → 尝试按整合包安装
            └─ :1080  ModpackInstall(FilePath)
                 :1081  FrmMain.PageChange(TaskManager)  ← 切到「任务管理」页，即“后台安装”
```

`FileDrag` 是 PCL 所有拖拽的总分发器，优先级依次为：

1. `.xaml` → 作为主页自定义文件
2. jar/litemod 等 → `PageInstanceMod.InstallMods`（拖入 Mod）
3. `zip / rar / mrpack` → **`ModpackInstall`**（本链条的主角）
4. `.rar` → 报错提示（不支持 rar）
5. 其他 → 尝试按崩溃报告分析

> 注意：`ModpackInstall` 抛异常时 `FileDrag` 会吞掉并继续尝试后续分支（:1083-1090），因此整合包安装失败的表现可能是「没有任何反应」。

---

## 2. 整合包类型识别

`ModModpack.vb:42-77`：用 `FileUtils.OpenZip` 打开压缩包（`rar` 只是扩展名，实际是 zip 的也能处理），按**根目录**、再按**一级目录**依次探测特征文件：

| 特征文件 | PackType | 类型 |
|---|---|---|
| `mcbbs.packmeta` | 3 | MCBBS 整合包 |
| `mmc-pack.json` | 2 | MultiMC / Prism |
| **`modrinth.index.json`** | **4** | **Modrinth 整合包（.mrpack）** |
| `manifest.json`（无 `addons` 字段） | 0 | CurseForge |
| `manifest.json`（有 `addons` 字段） | 3 | MCBBS |
| `modpack.json` | 1 | HMCL |
| `modpack.zip` / `modpack.mrpack` | 9 | 带启动器的压缩包 |
| 以上都没有 | -1 | 当作普通压缩包（`InstallPackCompress`） |

识别完成后 :88 有总开关拦截（`RestrictMcDownload`，社区版已关闭），:89-111 分派到对应安装函数。
`.mrpack` 走 **`InstallPackModrinth`（:366）**。

---

## 3. Modrinth 分支：`InstallPackModrinth` 全流程

### 步骤 1：解析 `modrinth.index.json`（:371-395）

```json
{
  "name": "整合包名",
  "dependencies": {
    "minecraft": "1.20.1",
    "forge": "47.2.0",          // 或 neoforge / fabric-loader
  },
  "files": [
    { "path": "mods/xxx.jar",
      "hashes": { "sha1": "..." },
      "downloads": ["https://cdn.modrinth.com/data/.../xxx.jar"],
      "env": { "client": "required|optional|unsupported" } }
  ]
}
```

- `dependencies.minecraft` 缺失 → 直接报错（:375）
- `forge` / `neoforge` / `fabric-loader` 写入 `TargetVersion.ModLoaderVersions`（:383-391）
- **未知依赖名** → `NotifyIncompatibleLoader`（:154）弹窗询问：取消 or 跳过该加载器继续

### 步骤 2：确定实例名与目录（:397-404）

- 优先用 JSON 的 `name`，经 `ValidateFolderName` 清洗
- 不合法或为空 → 弹输入框让用户命名
- 最终目录：`<MC 文件夹>\versions\<实例名>\`

### 步骤 3：解压 + 覆写目录（:406-413）

```
ExtractModpackFiles(temp, mrpack)            # 整个 zip 解压到任务临时目录
CopyOverrideDirectory(temp/overrides, 版本目录)       # 1. 复制文件
CopyOverrideDirectory(temp/client-overrides, 版本目录)
```

`CopyOverrideDirectory`（:126）在复制后还会：

- 写入 `PCL\Setup.ini`：`VersionArgumentIndie = 1`（**开启版本隔离**）、`VersionArgumentIndieV2 = True`
- 如果整合包自带 `overrides/PCL/Setup.ini`，会合并覆盖并复制到版本目录（:139-143）

### 步骤 4：把每个文件转成「候选 URL 列表」（:415-445）—— 镜像介入点

```vb
For Each File In Json("files")
    ' ① 客户端环境过滤
    env.client = optional    → 弹窗问是否下载
    env.client = unsupported → 跳过

    ' ② 生成候选 URL
    Urls = File("downloads").SelectMany(ParseCurseForgeDownloadUrls)   ' 官方 CDN 的多域名变体
    Urls.AddRange(Urls.Select(DlSourceModGet))                        ' ★ 追加镜像源
    Urls = ParseModrinthTrackArguments(Urls, ...)                     ' 追加 mr_* 追踪参数
    Urls = Urls.Distinct()

    ' ③ 路径安全检查：必须落在版本目录内（防 zip 路径穿越，:436）
    ' ④ 建任务：sha1 校验，不校验大小（代码注释：Modrinth 官方要求）
    FileList.Add(New NetFile(Urls, TargetPath, New FileChecker With {.Hash = sha1}, True))
Next
```

**关键点**：候选顺序 = `[官方 CDN 变体..., MCIM 镜像变体...]`。
即默认先试官方 CDN，失败才换镜像（除非网络判定为「不优先官方」，见后文）。

### 步骤 5：并行挂两条任务链（:443-455）

```vb
LoaderDownload("下载额外文件", FileList)        # 链 A：整合包内的 Mod/资源

McInstallLoadersGet(McInstallRequest{           # 链 B：原版 + 加载器
    NewInstanceName = 实例名,
    InstanceFolder  = 版本目录,
    Components      = McComponents(MC版本, 加载器版本)
})
```

两条链是 **`LoaderCombo` 里并列的两个子加载器**，同时进行——这就是「下载 Mod 的同时在装游戏」。

### 步骤 6：收尾（:456-476）

- 写入自定义图标（若从资源页安装，带 `Logo` 参数）
- 删除版本目录里的 `原始整合包.zip/.mrpack`
- 若整合包文件本身名为 `modpack.*`，也会被删除（下载页安装时会先落到临时目录）

### 步骤 7：后台任务展示（:486-489）

```vb
Loader.Start(VersionFolder)
LoaderTaskbarAdd(Loader)              # 加入任务管理页列表
FrmMain.BtnExtraDownload.ShowRefresh()
```

---

## 4. 下载源 / 镜像体系

### 4.1 Mod 文件镜像（MCIM）

`ModDownload.vb:1304`：

```vb
Public Function DlSourceModGet(Original As String) As String
    Return Original.
        Replace("api.modrinth.com",       "mod.mcimirror.top/modrinth").
        Replace("staging-api.modrinth.com", "mod.mcimirror.top/modrinth").
        Replace("cdn.modrinth.com",       "mod.mcimirror.top").
        Replace("api.curseforge.com",     "mod.mcimirror.top/curseforge").
        Replace("edge.forgecdn.net",      "mod.mcimirror.top").
        Replace("mediafilez.forgecdn.net","mod.mcimirror.top").
        Replace("media.forgecdn.net",     "mod.mcimirror.top")
End Function
```

- 纯**字符串域名重写**，不涉及 API 中转；镜像站按原路径回源
- 对 Modrinth CDN：`cdn.modrinth.com/data/<id>/versions/<ver>/xxx.jar` → `mod.mcimirror.top/data/...`
- 该函数也用于资源浏览页的图标/下载（`ResourceProject.vb:617`、`ResourceVersion.vb:137/178`）

### 4.2 原版与加载器镜像（BMCLAPI）

| 函数 | 作用 | 被重写的域名 |
|---|---|---|
| `DlSourceLauncherOrMetaGet`（:1291） | 版本 JSON、client.jar、Meta | launchermeta / launcher / piston-data / piston-meta |
| `DlSourceLibraryGet`（:1261） | libraries（Forge/Fabric/NeoForge 等） | libraries.minecraft.net 等 → `/maven` 或 `/libraries` |
| `DlSourceAssetsGet`（:1248） | 游戏资源（音效、语言、贴图索引） | resources.download.minecraft.net → `/assets` |

每个函数都返回 **`IEnumerable(Of String)`**，即「多个候选地址」，由 `DlSourceOrder`（:1226）决定顺序：

```vb
Return If(DlSourcePreferMojang, OfficialUrls.Union(MirrorUrls), MirrorUrls.Union(OfficialUrls))
```

**特殊处理**：Forge / Fabric / NeoForge 的 maven 资源（:1262）**不提供官方源**，直接给两个 BMCLAPI 路径——因为官方 maven 在国内基本不可用。

### 4.3 版本列表源与自动切换

`DlClientListLoader`（:196）是主加载器，由 `DlSourceLoader`（:1316）做「多源赛跑 + 定时升级」：

| `ToolDownloadVersion` | 顺序 | 失败后等待 |
|---|---|---|
| 0 尽量镜像 | BMCLAPI(30) → Mojang(90) | 30 个周期后升级到下一源 |
| 1 自动（默认） | Mojang(5) → BMCLAPI(35) | 5 个周期 |
| 2 尽量官方 | Mojang(60) → BMCLAPI(120) | 60 个周期 |

「自动」档的判定来自 `DlPreferMojang`（:239-242）：**首次请求 Mojang 版本列表耗时 < 4000ms 就视为官方源可用**，之后文件下载也走官方优先。

> BMCLAPI 版本列表加载器还会合并 `PathTemp\Cache\download.json`（:279），用于「PCL 特供下载」版本项——这是官方版的运营入口，开源版没有。

### 4.4 优先级设置的真实语义

设置项（`PageSetupSystem.xaml:27-37`）：

- **文件下载源** `ToolDownloadSource`：0=尽量镜像 / 1=自动 / 2=尽量官方
- **版本列表源** `ToolDownloadVersion`：同上
- 默认都是 **1（自动）**

### 4.5 镜像专属的「单线程黑名单」

`ModNet.vb:853-854`：

```vb
If TargetUrl.Contains("pcl2-server") OrElse TargetUrl.Contains("meloong.com") OrElse
   TargetUrl.Contains("bmclapi") OrElse TargetUrl.Contains("github.com") OrElse
   TargetUrl.Contains("optifine.net") OrElse TargetUrl.Contains("momot.rs") Then Return Nothing
```

命中这些域名的下载**不做多线程分片**（只单线程），避免镜像站限流/不支持 Range。
另外：BMCLAPI 源每新建一个线程会 `Thread.Sleep(100)`（:1744），主动降低请求频率。

---

## 5. 下载引擎（NetManager / NetFile / NetThread）

这是 PCL 下载的核心，值得单独理解。

### 5.1 三层结构

```
LoaderDownload（一个「批量下载任务」，如“下载额外文件”）
  └─ NetFile（一个文件）
       ├─ Sources: List(Of NetSource)   ← 该文件的所有候选 URL
       ├─ Check: FileChecker            ← sha1/大小校验
       └─ Threads: NetThread 链表       ← 该文件的多线程分片
            └─ NetThread（一段 Range 区间）
```

### 5.2 分片与并发

- 全局线程上限：`NetTaskThreadLimit = ToolDownloadThread + 1`（默认 64，设置里最大 255）——`ModNet.vb:311`
- 分片策略：从最大的未完成分片里切走 `40%`（`FilePieceMax.DownloadUndone * 0.4`，:862）
- 每线程用 HTTP `Range` 头请求（:917），下载到 `PathTemp\Download\<任务>_<线程>_<随机>.tmp`（:984），最后合并
- 校验文件大小 > 50MB 时先检查磁盘空间（:951-961）

### 5.3 失败换源（重点）

`SourceFail`（:1070）的判定与动作：

| 情况 | 动作 |
|---|---|
| 404 / 502 / 416 / 不支持 Range / 无返回数据 / 空间不足 | **禁用该源** |
| 403 / 429（且源不是 bmclapi） | 禁用该源（bmclapi 的 403/429 是限流，不禁用） |
| 连续失败次数超过阈值 | 禁用该源 |
| 还有其它可用源 | 继续（其它线程会自动切到下一个源） |
| 所有源都失败且未重试过 | **进入「单线程逐个重试」模式**：清空失败标记，把所有源标记为「不允许断点续传」，逐个从头下载 |
| 单线程重试后仍失败 | 整个文件判定失败 |

设计意图（代码注释原话）：兼容「多源中部分返回坏文件」「部分多线程会抽风」的镜像。

### 5.4 续传

- 同一次运行内：`Reset()` 保留已下载分片，失败线程可以从 `Thread.DownloadStart + DownloadDone` 续传（:843-845）
- 跨启动续传：**不支持**。本次会话的第一个下载任务启动时会清空 `PathTemp\Download`（`NetManager.Start`，:1802-1816），重启后需重新下载未完成部分
- 已完整下载并通过校验的文件不会重下（见 5.5）

### 5.5 校验

`FileChecker`（`MeloongCore/Shared/Utils/FileUtils.cs:503`）：

- 按哈希字符串长度自动识别 MD5(32) / SHA1(40) / SHA256(64) / SHA512(128)
- 支持 `ActualSize`（必须等于）与 `MinSize`（至少）
- 下载前先检查本地已有文件（`NetState.WaitingToCheck`），匹配则直接跳过——**已装过的 Mod 不会重复下载**

---

## 6. 游戏与加载器安装（链 B）

`McInstallLoadersGet`（`ModDownloadLib.vb:1733`）根据 `McComponents` 组装加载器列表：

| 加载器 | 实现函数 | 下载内容 |
|---|---|---|
| 原版 | `McDownloadClientLoader`（:9） | 版本 JSON → client.jar → libraries → assets |
| Forge | `McDownloadForgelikeLoader` | installer.jar + 解析出 libraries |
| NeoForge | 同上（`IsNeoForge=True`） | 同上 |
| Fabric | `McDownloadFabricLoader` | fabric-loader JSON + libraries |
| LiteLoader | `McDownloadLiteLoaderLoader` | LiteLoader 库 |
| OptiFine | `McDownloadOptiFineLoader` / `OptiFineSaveLoader` | 有加载器时作为 Mod 放进 mods |

原版安装细节（`McDownloadClientLoader`）：

1. `DlClientListGet(Id)` 拿到版本 JSON 地址 → `DlSourceLauncherOrMetaGet` 生成候选
2. 下载 JSON（`IsJson` 校验）
3. `McLibNetFilesFromInstance` 分析 libraries → `DlSourceLibraryGet` 生成候选
4. `DlClientAssetIndexGet` + `McAssetsFixList` 分析资源 → `DlSourceAssetsGet` 生成候选
5. 全部并行下载

最后 `安装游戏` 任务（:1837）：

- `MergeJson(...)` 把原版/Forge/NeoForge/Fabric/LiteLoader/OptiFine 的版本 JSON 合并成一个
- `Copy(temp/libraries → 全局 libraries)`
- 计算版本隔离目录并创建 `mods\`、`resourcepacks\`
- 删除临时图标 `.pclignore` 标记文件

---

## 7. 可插拔点：接入自有镜像要改哪里

| 目标 | 改动位置 | 说明 |
|---|---|---|
| 替换 Mod/整合包文件镜像 | `ModDownload.vb:1304 DlSourceModGet` | 纯 URL 重写，返回一个字符串；也可改成返回多个候选 |
| 替换原版/加载器镜像 | `DlSourceLibraryGet` / `DlSourceAssetsGet` / `DlSourceLauncherOrMetaGet` | 返回 `IEnumerable(Of String)`，可加入自己的域名 |
| 调整官方/镜像优先顺序 | `DlSourceOrder`（:1226）与 `ToolDownloadSource` 设置 | |
| 版本列表走自建源 | `DlClientListMojangLoader` / `DlClientListBmclapiLoader`（:232/:273） | 直接换 URL 即可 |
| 注入「特供下载」版本项 | `DlClientListBmclapiMain` 合并 `PathTemp\Cache\download.json`（:279） | 开源版无此功能，可自建 |
| 私有 CDN 鉴权 | `ModSecret.vb:19 SecretCdnSign` + `SecretHeadersSign` | URL 里的 `{CDN}` 占位符与签名头，开源版为空实现 |
| 下载引擎行为 | `NetFile.SourceFail` / `NetManager.StartManager` | 线程数、限速、换源策略 |

---

## 8. 实测数据（本机，2026-11-25）

| 端点 | HTTP | 耗时 |
|---|---|---|
| `launchermeta.mojang.com/mc/game/version_manifest.json` | 200 | 975 ms |
| `bmclapi2.bangbang93.com/mc/game/version_manifest.json` | 302（重定向） | 1110 ms |
| `api.modrinth.com/v2/project/fabric-api` | 200 | 1240 ms |
| `mod.mcimirror.top/modrinth/v2/project/fabric-api` | 200 | **391 ms** |
| `mod.mcimirror.top/` | 200 | 944 ms |

结论：国内环境下 MCIM 镜像对 Modrinth API 的加速约 3 倍；Mojang 官方目前可直连（决定 `DlPreferMojang` 是否成立）。

---

## 9. 注意事项与风险

1. **`mod.mcimirror.top` 是第三方公益镜像**，文件内容依靠 `sha1` 校验兜底；若镜像返回内容与 sha1 不符，`FileChecker` 会拒绝并换源。
2. **mrpack 的文件不校验大小**（代码明确注释「Modrinth 官方说的」），只校验 sha1。
3. **zip 路径穿越已有防护**（:436），但仍建议对第三方整合包保持警惕。
4. **镜像解析是纯字符串替换**：若未来 Modrinth/CurseForge 改域名或路径结构，`DlSourceModGet` 会失效，需要同步更新。
5. **单线程黑名单**按域名子串匹配，自建镜像若走 CDN 域名，需要相应调整才能享受多线程。
6. **跨启动无断点续传**，大整合包中断后需重下未完成部分（已完成文件靠校验跳过）。
