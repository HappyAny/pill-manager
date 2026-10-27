# 0.2.1 单文件与图标验证

验证日期：2026-10-26。发布目标：Windows x64。

## 交付物

- `dist/standalone/PILL.exe`：71,409,709 字节，约 71.4 MB（68.1 MiB）。分发目录只有这一个文件。
- 内置 .NET Core 和 Windows Desktop Runtime 10.0.14，不需要额外安装 .NET。
- EXE 的 Windows 图标资源包含 16、20、24、32、40、48、64、128、256 像素九种尺寸。WPF 窗口引用同一个图标。
- “关于与许可证”内置本项目及第三方依赖的七份许可文档；源码包保留原文。

EXE SHA-256：

```text
a77144ffafdaa97d9f9faf7a4f86d7d7761d5b9a3c8a880b4374b14c3f4410fd
```

单文件表示只需分发一个 EXE。运行时仍会产生 .NET 本机组件缓存；应用备份目录见 README。旧版目录和个人备份均保留。

## 已完成的检查

1. 发布构建成功，无警告或错误。构建元数据列出的两个内置运行时均为 10.0.14。
2. 解析发布 EXE 的 PE 资源，确认九种图标尺寸及图标组存在。
3. 仅复制 EXE 到一个新目录，将 `DOTNET_ROOT` 等变量指向空目录，并将 `PATH` 限为 Windows System32。
4. 在该目录运行 EXE 的验证入口，正常退出（退出码 0）。.NET 主机日志明确报告 `Executing as a self-contained app` 及 `is_framework_dependent=0`；五个 WPF 本机 DLL 从包内提取到独立测试缓存。
5. 窗口图标加载成功，七份内置许可均可读取。浅色、深色和最小窗口共生成九张界面图。
6. 本机 BLE 扫描及 Core Audio 枚举成功，发现一台 PILL 和两个活动音频端点。仅执行发现与枚举，没有连接设置通道或写入耳机、音量设置。
7. 二十项离线协议检查全部通过，包括帧解析、EQ 数值与长度边界、提示音档位换算。
8. 发布包和源码包通过 ZIP CRC 检查，发布包包含 SHA-256 清单。私有备份、设备标识日志、反编译研究文件与构建缓存不进入发布包。

机器可读记录：`portable-build-0.2.1.json`、`portable-check-0.2.1.json`。发行包收据位于 `dist/package-receipt-0.2.1.json`。

## 复核命令

```powershell
python tools/build_portable.py
dotnet run --project tests/PillManager.Checks.csproj -c Release
# 验证脚本需要开发环境中的 pefile 和 Pillow；EXE 运行不需要 Python
python tools/verify_portable.py
python tools/package.py
```

若已具备完整 NuGet 缓存目录，构建命令支持 `--offline-feed <目录>`。验证脚本的 `--preview-ui --verify-native` 调用会进行只读蓝牙扫描与音频枚举；不执行硬件设置写入。界面图使用明确标注的示例数据。

## 验证边界

以上检查在当前 Windows 主机完成，不代表全新 Windows 虚拟机或所有蓝牙适配器均已验证。图标已从 EXE 资源和窗口加载结果验证，没有人工逐项检查不同 Windows 缩放比例下的任务栏外观。此版本未重做耳机设置写入测试；此前实际设备的设置变更与恢复记录见 `VERIFICATION.md`。无代码签名。
