# 第三方组件声明（THIRD-PARTY NOTICES）

本项目的联机功能（`Limlin.Link`）在工程实现上移植自 **Campfire** 的联机模块，
其原始项目以 **GPL-3.0** 授权发布：

- 项目：Campfire（Xocio/Campfire）
- 许可：GNU General Public License v3.0
- 源码地址：https://github.com/Xocio/Campfire

移植说明：

1. 本仓库中的 `Limlin.Link/` 目录下的联机核心（邀请码、房间协议帧、EasyTier 配置与运行、
   局域网端口发现、假局域网通告等）源自上述项目，并做了命名空间、品牌文案、
   依赖裁剪与启动器接入改造；
2. 依据 GPL-3.0 第 2 条，**不对外分发（convey）的构建不受其开源义务约束**；
   若将来公开发布本构建，需要以 GPL-3.0 兼容方式提供对应源码，或移除该目录的衍生实现；
3. 运行期下载的 **EasyTier** 以 LGPL-3.0 授权，以独立进程方式调用：
   https://github.com/EasyTier/EasyTier

其他第三方：

- Newtonsoft.Json、NAudio 等沿用原 PCL 项目的声明与许可。