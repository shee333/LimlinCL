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

已配置的生产环境：

| 项 | 值 |
|---|---|
| 存储桶 | `limlinlauncher-1254351530` |
| 地域 | `ap-shanghai` |
| 访问地址 | `https://limlinlauncher-1254351530.cos.ap-shanghai.myqcloud.com/launcher/community/servers.json` |
| 访问权限 | 公有读私有写（匿名 GET 已验证可用） |

客户端默认源已写死在 `Plain Craft Launcher 2\Modules\ModCommunitySync.vb` 的 `DefaultSourceUrls`，可填多个作为主备。

> 后续如果要用自定义域名 + CDN 加速：域名需完成 ICP 备案，再绑定 CDN、回源到 COS。

## 4. 发布流程

### 方式 A：脚本（推荐，无需 coscli）

脚本直连 COS REST API（v5 签名），只要 `tools\cos.local.json` 存在即可：

```powershell
# 校验 + 自动 revision+1 + 上传 + 回读校验
.\tools\publish-servers.ps1 -Path .\servers.json -Bump -Upload
```

`tools\cos.local.json`（**已被 .gitignore 忽略，不会提交**）：

```json
{
  "bucket": "limlinlauncher-1254351530",
  "region": "ap-shanghai",
  "secretId": "AKID...",
  "secretKey": "...",
  "keyPrefix": "launcher/community/"
}
```

也支持环境变量覆盖：`LIMLIN_COS_BUCKET`、`LIMLIN_COS_REGION`、`LIMLIN_COS_SECRET_ID`、`LIMLIN_COS_SECRET_KEY`、`LIMLIN_COS_KEY_PREFIX`。

> ⚠️ 注意：`servers.json` 必须保存为 **UTF-8 无 BOM**。带 BOM 时客户端解析会失败（脚本已自动处理，读取去 BOM、写回不带 BOM）。

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
