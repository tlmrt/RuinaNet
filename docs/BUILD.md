# 从源码构建

## 环境

- Windows，PowerShell，.NET Framework 4.x 自带的 C# 编译器（代码保持 C# 5 兼容）。
- 已合法安装《废墟图书馆》。找到 `LibraryOfRuina_Data/Managed`。
- 自行提供 HarmonyX 2.9.0 的 `0Harmony.dll`；其运行依赖为 Mono.Cecil 0.10.4、MonoMod.RuntimeDetour 22.1.29.1、MonoMod.Utils 22.1.29.1。来源及许可证见仓库对应说明。
- 不要将游戏或 Steam 程序集提交到仓库。

## 编译

在仓库根目录运行，替换路径为自己的合法安装位置：

```powershell
./build.ps1 -ManagedPath '游戏目录/LibraryOfRuina_Data/Managed' -HarmonyPath '依赖目录/0Harmony.dll'
```

也可设置环境变量 `RUINA_MANAGED`、`RUINA_HARMONY`。输出 `build/RuinaNet.dll`。

## 检查

无需游戏与 Steam 账号的逻辑及传输边界检查：

```powershell
./build-tests.ps1 -SkipNative
```

包含实际游戏方法与补丁安装的完整检查（先完成构建）：

```powershell
./build-tests.ps1 -ManagedPath '游戏目录/LibraryOfRuina_Data/Managed' -HarmonyPath '依赖目录/0Harmony.dll'
```

目前共 712 项断言：644 项原有逻辑检查、26 项实际游戏契约、42 项新增 Steam 传输与生命周期检查。Steam 检查使用受控 API 边界，执行的是生产版 SteamLink、NetLink、ReliableChannel；它不建立真实 Steam 房间，也不替代两台电脑、两个账号的网络验收。

## 手动制作模组目录

创建 `RuinaNet/Assemblies`，放入本模组 DLL 与上述四个第三方 DLL，再将 `StageModInfo.xml` 放入 `RuinaNet` 根目录。使用原版接待编辑器时，还需创建：

```text
Data/StoryEffect
Data/StoryText
Resource/CharacterSkin
Resource/CombatPageArtwork
Resource/StoryBgm
Resource/StoryBgSprite
Resource/StoryStanding
Resource/MotionSound
```

空目录可放 `_keep.txt` 保留。否则原版编辑器可能因缺目录报错，保存与上传按钮一直灰色。元数据所有 InvitationFile 项为空是正常的，本模组不自带接待关卡。

生成或选择自己的封面，再在编辑器设置封面路径。保存时 XML 的 Version 可能被编辑器重写成格式版本 1.1；实际模组版本以初始化日志为准。

## v1.0 实战验收清单

1. 两台电脑、两个 Steam 账号，同版本创建房间、邀请、接受及手动编号加入。
2. 确认版本不一致、第三人、非房间成员无法进入本场；取消加入后迟到结果不重新开房。
3. 普通接待的分工、配装、每幕确认、拼点与结算。
4. 异想体首次挑战与重战的准备、剧情结束等待及结算。
5. 一端离开或断线后结束当前场次，重新连接；不应迁移房主或误接入上一房间。
6. Steam 不可用时切换到 UDP，确认直连仍正常。

以上均尚待真实双账号验收，不能仅根据自动检查勾选通过。