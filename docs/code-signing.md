# 代码签名与 SmartScreen 说明

## 1. 为什么会出现「未知发布者」

Windows 对下载的 exe 运行 **Authenticode 签名**检查：

- 没有签名 → SmartScreen 弹「Windows 已保护你的电脑 / 未知发布者」
- 有签名但证书不受信任（自签名）→ 同样警告
- 有 CA 签发且已积累信誉的签名 → 不警告

我们当前产物是**未签名**的（`Get-AuthenticodeSignature` 结果 `NotSigned`），所以必然触发警告。这不是程序有问题，纯粹是「没有可验证的发布者身份」。

## 2. 证书方案对比（2026-11 调研）

| 方案 | 价格 | SmartScreen 表现 | 说明 |
|---|---|---|---|
| **OV 代码签名证书** | 约 ¥3500~4900/年（沃通标准版 ~¥3588） | 初期仍可能警告，**随下载量/时间积累信誉后消失** | ✅ **推荐**，性价比最高 |
| EV 代码签名证书 | 约 ¥4288~6888/年 | ⚠️ **自 2024 年起与 OV 相同**，不再保证首日跳过 | 溢价意义已大幅下降 |
| 自签名证书 | 免费 | 无效（用户需手动信任） | ❌ 只适合内部测试 |
| Azure Trusted Signing | $9.99/月 | 与 OV 同 | 微软云签名，需确认中国大陆主体是否可开通 |
| 微软商店分发 | 免费 | SmartScreen 不拦（商店渠道） | 分发方式要改，需过审核 |

> 微软官方文档明确写道：EV 证书自 2024 年起 SmartScreen 行为 **与 OV 相同**（"Same as OV since 2024"）。
> 参考：<https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options>
> 国内价格参考：<https://buy.wosign.com/CodeSigning>

### 结论

1. **买一张 OV 代码签名证书**（¥3500 左右/年）即可，不必为 EV 多花钱
2. 签名后需要**积累信誉**（通常几周，取决于下载量），期间部分用户可能仍见警告
3. 若急于一上线就零警告，可考虑**微软商店分发**（但要改分发渠道）
4. 购买时注意：
   - 需要**企业/组织主体**（营业执照），个人一般无法申请 OV/EV
   - 多数 CA 要求 **D-U-N-S 编码**（邓白氏），办理约 1~2 周
   - EV/部分 OV 会寄送**硬件 Key**，云签名服务（如 SSL.com eSigner、DigiCert KeyLocker）可免硬件

## 3. 证书到位后如何接入（已内置到打包脚本）

### 3.1 配置方式一：证书已装入 Windows 证书存储（EV 硬件 Key / 云签名）

把证书导入到「当前用户 → 个人」或「本地计算机 → 个人」，然后编辑 `tools\signing.local.json`：

```json
{
  "thumbprint": "证书指纹（40 位十六进制，去掉空格）",
  "timestampUrl": "http://timestamp.digicert.com",
  "hashAlgorithm": "SHA256"
}
```

### 3.2 配置方式二：PFX 文件

```json
{
  "pfxPath": "D:\\certs\\limlin-code-signing.pfx",
  "pfxPassword": "证书密码",
  "timestampUrl": "http://timestamp.digicert.com",
  "hashAlgorithm": "SHA256"
}
```

> `signing.local.json` 已加入 `.gitignore`，**不会提交**（含私钥密码）。
> 模板见 `tools/signing.local.example.json`。

### 3.3 也可以用环境变量临时覆盖

```powershell
$env:LIMLIN_SIGN_THUMBPRINT = "ABCD...40位"
$env:LIMLIN_SIGN_PFX = "D:\certs\xxx.pfx"
$env:LIMLIN_SIGN_PFX_PASSWORD = "密码"
$env:LIMLIN_SIGN_TIMESTAMP = "http://timestamp.digicert.com"
```

环境变量优先级高于 `signing.local.json`。

### 3.4 打包即自动签名

双击 `build-release.bat` 后脚本会自动：

1. 编译 Release
2. 复制到 `product\LimlinLauncher-<版本>.exe`
3. **若配置了证书 → 调用 `Set-AuthenticodeSignature` 签名 + 时间戳**
4. 输出签名状态与 SHA256

未配置证书时只提示并跳过，不影响出包。

## 4. 如何验证签名是否成功

```powershell
Get-AuthenticodeSignature "D:\MiencraftDEV\LimCL\product\LimlinLauncher-0.1.10.exe" |
    Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate
```

- `Status = Valid` → 签名正确且证书链受信任
- `Status = UnknownError` → 签名存在但根证书不受信任（自签名会这样，正式证书不会）
- `Status = NotSigned` → 未签名

**时间戳很重要**：带时间戳的签名在证书到期后仍然有效，所以务必保留 `timestampUrl`。

## 5. 分发时的配套说明（无论是否签名）

建议在发布页同时提供：

1. **SHA256 校验值**（打包脚本会输出）
2. 首次运行提示文案：

> 若出现「Windows 已保护你的电脑」，点击「更多信息」→「仍要运行」。
> 这是 Windows 对未签名/新发布程序的常规提示，不是病毒警告。
> 校验方式：`certutil -hashfile LimlinLauncher-x.y.z.exe SHA256`，与发布页数值一致即可。

3. 若文件来自网盘/浏览器下载，可指导用户：右键 exe → 属性 → 勾选「解除锁定」→ 确定（消除 Mark-of-the-Web，可减少部分拦截）

## 6. 采购建议清单（给运营）

- [ ] 确认用哪个主体申请（公司/组织营业执照）
- [ ] 申请 D-U-N-S 编码（若 CA 要求）
- [ ] 比价：沃通（国内、支付宝/微信支付）vs DigiCert/Sectigo 国内代理
- [ ] 确认是否支持**云签名**（免硬件 Key 邮寄，适合 CI 自动化）
- [ ] 证书有效期 1 年，到期前 1 个月续费
- [ ] 拿到证书后：配置 `tools\signing.local.json` → 跑一次打包 → 用 `Get-AuthenticodeSignature` 确认 `Status=Valid`
- [ ] 发布首批签名版本后，观察 SmartScreen 警告消失时间（通常数周）
