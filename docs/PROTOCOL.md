---
title: MOONDROP PILL Windows BLE 管理协议实测笔记
kind: analysis
domains:
  - electronics
  - software-engineering
status: draft
verification: mixed
created: 2026-10-26
updated: 2026-10-26
---

# MOONDROP PILL：Windows 管理通道与验证边界

本笔记来自 2026-10-26 的单台 PILL、固件 1.6.0 实测。结论只覆盖该样本，不代表其他硬件批次和固件。设备地址、主机名和个人配置不收录。

## 通道与帧

Windows 的普通蓝牙音频连接和 BLE 管理连接彼此独立。实机管理服务为 `00001100-d102-11e1-9b23-00025b00a5a5`，TX 为尾号 `1101`，RX/Notify 为 `1102`。没有在此样本上找到某些社区资料所推测的 9ECA 管理服务。

命令帧为 `uint16BE vendor=0x001D`、`uint16BE word`、负载。`word = feature << 9 | type << 7 | command`。type 为 0 命令、1 通知、2 回复、3 错误。请求串行化，并匹配 feature/command/type。电量查询的完整帧为 `001D1A010102`。

此固件的长 TX 写入会静默截断；软件把单个完整 TX 报文限制为 20 字节以内。RX 可以是更长的完整通知，不能用相同限制截断读取。该阈值是实机规避策略，不是所有 BLE 设备的普遍 MTU。

协议无独立事务 ID。查询超时后会停止当前管理会话继续写入，要求重新连接，避免把迟到回复用于下个请求。发送设置后的 ACK 不作为成功的唯一依据：软件重新查询设置并核对负载。

## 已核对的功能

| feature | 命令 | 格式与行为 |
|---|---|---|
| 0 基本信息 | GET 1 / 4 / 5 | 1 能力列表，首字节 0，后续 feature/version 对；4 型号字符串；5 固件字符串 |
| 13 电量 | GET 1，负载 01 02 | 回复按 id/百分比成对；1 左耳、2 右耳；0 有效，FF 未知 |
| 5 EQ | GET 1 / 2 / 4 / 5；SET 3 / 6 | 预设列表、当前预设、段数、参数；切换预设、写参数 |
| 1 触控 | GET 2 / SET 3 | 12 字节：启用标记 + 11 个动作 |
| 15 增益 | GET 1 / SET 2 | 单字节设备档位 0、1、2；界面不猜测增益 dB |
| 20 双设备 | GET 1 / SET 2 | 单字节布尔；GET 3 读超时字段；GET 5/6 读取连接设备标识 |
| 14 提示音 | GET 1 / SET 2 | 3 字节：启用、音量、音色索引；程序保持索引不变 |

能力列表中的 feature/version 是协议版本，不是耳机固件号。标准电池服务 180F 在本次读取中没有返回可用电量；左右耳采用厂商协议读取。没有充电盒电量的有效证据。

## 五段 EQ

GET 5 的负载为起止段号 `00 04`。返回为 `[start, end, preGain:i16BE]`，接每段 7 字节：`frequency:u16BE, Q:u16BE, filter:u8, gain:i16BE`。preGain 和 gain 以 1/60 dB 为单位；Q 的比例为 1/4096。样本返回 5 段，filter 都为 0。

单段 SET 6 为 `[i, i, preGain:i16BE, one-band:7 bytes]`，连同帧头共 15 字节。写完五段后重新读全量并逐字节核对，再选自定义预设 63。预设 0 是关闭 EQ；1～4 的具体声音语义未获得足够可靠的设备级证据，保留编号。

开发时曾尝试一次完整 43 字节 TX，固件静默截断并改变后续 EQ 数据。检测到读回不符后，改为逐段传输，用初始备份恢复；恢复后的全部 EQ 字节与初始备份一致。随后逐段变更与恢复回归通过。已加 20 字节硬限制，发布版不再发送该长 TX。

## 提示音量的非对称百分比

实测：SET 56 读回 50；SET 57 读回 56；SET 63 读回 62。直接把 GET 的百分比再 SET 回去会降低档位。

与这些样本一致的模型是 16 个间隔：档位 n 的 SET 值为 `ceil(100*n/16)`，GET 值为 `floor(100*n/16)`。界面使用 0～16 档，并展示耳机读回的百分比。50%、56%、62% 附近的变更和恢复已实测，未遍历整段量程；每次仍要求读回匹配，否则报错。最终原始 56% 已恢复。

## 触控与保留字段

每个动作字节的高半字节为 1 左耳、2 右耳、3 任一耳；低半字节为 0 未分配、1 单击、2 双击、3 三击、4 长按 1 秒、5 长按 3 秒。前九个动作为播放/暂停、上一曲、下一曲、音量增加、音量降低、接听/挂断、拒接、唤醒助手、取消助手。

官方通用类把最后两项命名为 ANC；该命名不能证明 PILL 的产品行为。因此软件保持这两个字节原值，不把它们宣称为 ANC 控制或游戏模式开关。配置字节已验证，实际敲击和听觉行为未逐项人工验证。

## 实现约束

扫描动态识别 PILL；写入前核对回复型号和固件，不硬编码设备地址。只向已验证的 1.6.0 开放写入。每次修改前重新读状态并写备份；备份失败则不写耳机。多条命令不具备原子性；EQ 提供连接仍在时的恢复尝试，但不承诺断电可自动恢复。

Windows 音量采用 Core Audio；只枚举名称含 PILL 且处于活动状态的端点。播放和麦克风音量均做了有限变更与恢复，实际端点不是系统默认设备的别名。用户自定义改名而不含 PILL 时不会被列出。

## 来源与使用方式

1. [水月雨 PILL 产品页](https://moondroplab.com/cn/products/pill)，用于确认产品功能范围。
2. [官方软件下载页](https://moondroplab.com/cn/download)及其 Android APK：本地核对 V3VoicePlugin、V3EarbudPlugin、V3MusicProcessingPlugin、V3OnebringtwoPlugin、BandInfo 等格式。官方二进制及反编译内容不归档、不分发。
3. [Mooncontroller，固定提交](https://github.com/guwen777/Mooncontroller/tree/681c2bb4b46c912fadb497e9073243d423a14138)：协议发现线索；其推测的 PILL 服务与本次样本不一致，未盲用。
4. [Edge2Pods 协议，固定提交](https://github.com/Konakonai/Edge2Pods/blob/ab3f799d2d899d78efdd922bc7201e2fef4994ea/EDGE2_PROTOCOL.md)：GAIA 与双设备字段的交叉核对，其他型号的结论不直接作为 PILL 证据。
5. [MoondropMirage 协议笔记，固定提交](https://github.com/cgluWxh/MoondropMirage/blob/bfb20969e742c856c9c5824a3ebadbac0d271e22/MIRAGE_PROTOCOL_FINDINGS.md)：Bluetrum EQ 与官方 APK 的交叉核对。
6. [Microsoft IMMDeviceCollection](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdevicecollection)；接口 GUID 与本机 Windows SDK 10.0.26100.0 的 mmdeviceapi.h 核对。

这些来源说明格式和实现方向。是否在本机生效，以本次实机读回及验证记录为准；不能据此声称音质、低延迟或长期稳定性已得到验证。
