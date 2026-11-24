# 社区服务器列表同步（P1）说明

## 1. 架构

```
管理员编辑 servers.json
        │
        ├── tools\publish-servers.ps1 -Bump          （校验 + revision+1）
        │                                   -Upload   （上传 COS，可选）
        ▼
腾讯云 COS（公有读）
  https://<bucket>-<appid>.cos.<region>.myqcloud.com/launcher/community/servers.json
        │
        ▼
客户端（PCL 社区游戏页）
  进页面且距上次检查 >1 小时 → 后台拉取 → 校验 → revision 比对 → 原子替换本地缓存
        │
        ▼
  PCL\Community\servers.json   本地缓存（渲染用）
  PCL\Community\meta.json      同步状态（lastCheck / revision / sourceUrl）
```

## 2. 客户端行为

| 项 | 实现 |
|---|---|
| 触发时机 | 首次进入「社区游戏」页；之后每次进入且距上次检查 ≥ 1 小时 |
| 手动触发 | 页面「检查列表更新」按钮（忽略 TTL，立即同步） |
| 更新判定 | 远端 `revision` > 本地 `revision` 才替换；相等提示「已是最新」；更旧则保留本地 |
| 校验项 | JSON 合法、`servers` 为数组、`revision` 正整数、`schemaVersion` ≤ 1、`minLauncherVersion` 检查、id 唯一、ip 非空、端口 0~65535 |
| 写入方式 | `FileUtils.WriteAtomic` 原子替换，写失败不会破坏旧文件 |
| 失败处理 | 网络失败 / 校验失败 → 保留旧数据，仅写日志；手动点击时弹红色提示 |
| 多源 | 按顺序尝试，任一成功即停 |

### 本地覆盖（测试用）

在 `PCL\Community\source.json` 写入：

```json
{ "sourceUrls": ["http://127.0.0.1:8765/servers.json"] }
```

即可临时指向别的地址（优先于内置默认源），测试完删除该文件即恢复。

## 3. 腾讯云 COS 配置（管理员一次性操作）

1. **创建存储桶**
   - 控制台 → 对象存储 → 存储桶列表 → 创建
   - 地域建议：`ap-guangzhou` 或 `ap-shanghai`
   - 访问权限：**公有读私有写**（客户端需要匿名 GET）
2. **创建目录前缀**：`launcher/community/`
3. **上传 `servers.json`**
   - 建议响应头：`Content-Type: application/json`、`Cache-Control: max-age=60`
4. **获取访问地址**（默认域名，免备案）
   ```
   https://<存储桶名>-<APPID>.cos.<地域>.myqcloud.com/launcher/community/servers.json
   ```
   在浏览器直接打开该地址，应返回 JSON 即配置成功。
5. **把这个地址填入客户端**
   - 代码位置：`Plain Craft Launcher 2\Modules\ModCommunitySync.vb` 中的 `DefaultSourceUrls`
   - 可填多个（主源 + 备用源）

> 后续如果要用自定义域名 + CDN 加速：域名需完成 ICP 备案，再绑定 CDN、回源到 COS。

## 4. 发布流程

### 方式 A：脚本（推荐）

```powershell
# 1. 编辑 servers.json（本机任意副本）
# 2. 校验 + 自动 revision+1
.\tools\publish-servers.ps1 -Path .\servers.json -Bump

# 3. 上传（需要 coscli + 环境变量）
$env:LIMLIN_COS_BUCKET = "limlin-1250000000"
$env:LIMLIN_COS_KEY    = "launcher/community/servers.json"
$env:LIMLIN_COS_URL    = "https://limlin-1250000000.cos.ap-guangzhou.myqcloud.com/launcher/community/servers.json"
.\tools\publish-servers.ps1 -Path .\servers.json -Bump -Upload
```

没装 coscli 时，脚本会打印手动上传步骤（COS 控制台拖拽上传即可）。

### 方式 B：控制台手动

1. COS 控制台 → 存储桶 → `launcher/community/`
2. 上传/覆盖 `servers.json`
3. 把文件里的 `revision` 手动 +1、`updatedAt` 改成当前时间

**每次发布必须递增 revision**，否则客户端会认为「已是最新」而不更新。

## 5. 需要提供给开发的信息

| 信息 | 示例 | 用途 |
|---|---|---|
| 存储桶名（含 APPID） | `limlin-1250000000` | 上传配置 |
| 地域 | `ap-guangzhou` | 拼访问域名 |
| 访问地址 | `https://limlin-1250000000.cos.ap-guangzhou.myqcloud.com/launcher/community/servers.json` | 写入客户端默认源 |
| （可选）子账号密钥 | SecretId / SecretKey | `-Upload` 自动化上传；建议只授予该前缀的 PutObject/GetObject 权限 |
| （可选）自定义域名 | `https://cdn.limlin.cn/...` | 后续 CDN 加速，需要备案 |

## 6. 排错

| 现象 | 排查 |
|---|---|
| 点「检查列表更新」无反应 | 看 `PCL\Log1.txt` 中 `社区列表同步` 相关行 |
| 提示「尚未配置社区列表更新源」 | `DefaultSourceUrls` 仍是占位地址（含 `YOUR-BUCKET`），或 `source.json` 写错 |
| 提示「列表校验失败」 | 按提示检查 JSON；常见：id 重复、缺 ip、端口越界、revision ≤ 0 |
| 提示「已是最新」但内容没变 | 发布时忘记递增 revision |
| 拉取到旧内容 | COS 缓存/CDN 缓存；确认对象已覆盖，必要时刷新 CDN 缓存 |
| 想确认同步状态 | 查看 `PCL\Community\meta.json` 的 `lastCheck` / `revision` / `sourceUrl` |

## 7. 安全建议

1. COS 桶只存放公开静态文件（列表 JSON），**不要放私密内容**
2. 上传用**子账号密钥**，仅授予 `launcher/community/*` 的 PutObject/GetObject 权限
3. 客户端侧有 schema + revision + id 唯一性校验；后续可再加 JSON 签名（公钥内置、私钥在 CI）
4. 不要在 `servers.json` 中放任何可执行内容或非 HTTPS 的 modpack 下载地址
