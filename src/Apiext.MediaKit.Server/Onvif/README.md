# Onvif 目录

ONVIF 设备接入：设备发现、连接/取流、云台（PTZ）与预置位控制。

> 与 ONVIF 设备的交互全部通过**原生 SOAP 报文**实现（`HttpClient` + `XDocument` 手工拼装/解析），
> 不引入第三方 ONVIF SDK。

## 文件说明

| 文件 | 职责 |
|---|---|
| `OnvifClient.cs` | ONVIF SOAP 客户端封装：WS-Security 认证、设备信息/流地址、PTZ 与预置位 |
| `OnvifDiscovery.cs` | WS-Discovery 设备发现（UDP 组播 Probe） |

## 关键实现点

### OnvifClient（`OnvifClient.cs`）

- **认证**：WS-Security UsernameToken（`PasswordDigest = Base64(SHA1(nonce + created + password))`），
  请求头带 `s:mustUnderstand="1"`，`nonce` 用 `RandomNumberGenerator` 生成。
- **服务地址约定**（海康/大华等绝大多数设备）：
  - Device：`/onvif/device_service`
  - Media：`/onvif/media_service`
  - PTZ：默认 `/onvif/ptz_service`，首次 PTZ 调用时再从 `GetCapabilities` 的 `PTZ.XAddr` 解析覆盖（解析失败回退默认路径）
- **已实现操作**：
  - 设备：`GetDeviceInformation`（厂商/型号/固件/序列号）、`GetCapabilities`（能力，解析 PTZ 服务地址）
  - 媒体：`GetProfiles`（Profile + PTZConfiguration token）、`GetStreamUri`（RTSP 流地址）、`GetSnapshotUri`（JPEG 抓拍地址）
  - PTZ：`GetPresets`（预置位列表）、`SetPreset`（保存当前位为预置位）、`RemovePreset`（删除预置位）、`GotoPreset`（跳转预置位）、`ContinuousMove`（连续移动）、`Stop`（停止）
  - `GetConfigurationOptions` → 解析速度空间 Range，用于**方向自适应**
- **命名空间**：PTZ 操作使用 `http://www.onvif.org/ver20/ptz/wsdl`（ver20），
  数据字段（PanTilt/Zoom 等）用 `http://www.onvif.org/ver10/schema`。

### 方向自适应

不同厂商 Pan/Tilt 轴的正向约定不一致（如海康 Y 正=上、大华 Y 正=下）。
通过 `GetConfigurationOptions` 读取 `ContinuousPanTiltVelocitySpace` / `ContinuousZoomVelocitySpace`
的 `XRange` / `YRange`，当 `Min > Max` 判定该轴反转，移动时自动翻转符号。
探测结果缓存在 `OnvifDevice` 记录（`PtzAxesResolved` 等字段），只探测一次。

### 预置位与抓拍

- 预置位管理闭环：`SetPreset`（把当前云台位置保存为新预置位，返回新 token，名称做 XML 转义）→
  `GetPresets`（列表）→ `GotoPreset`（跳转）/ `RemovePreset`（删除）。
- 抓拍：`GetSnapshotUri` 返回设备 JPEG 抓拍地址（Media 服务），前端在**应用内 Modal** 展示（避免新开窗口被拦截、且能给出失败说明）；
  部分设备对 snapshot 接口要求认证，直接浏览器访问可能返回 401，后续可按需加后端代理抓图。
- 云台 UI 不自绘：`Components/DeviceDetail/OnvifDetail.razor` 复用统一键盘 `Components/PtzPad.razor`
  （`ShowDiagonal="false" ShowFineControls="false"`，只保留上下左右与变倍），只额外提供速度滑杆，
  `PtzCommand` → `PtzDirection` 做一次映射，不支持的方向给提示。

### 模型

- `OnvifDeviceInfo`：设备信息
- `OnvifProfile`：Profile token + PTZConfiguration token
- `OnvifPreset`：预置位（token + 名称）
- `PtzDirection`：移动方向枚举（Up/Down/Left/Right/ZoomIn/ZoomOut）
- `PtzAxes`：各轴反转标记（Pan/Tilt/Zoom）

### OnvifDiscovery（`OnvifDiscovery.cs`）

- WS-Discovery：向组播地址 `239.255.255.250:3702` 发送 `Probe`（Type = `NetworkVideoTransmitter`），
  解析 `ProbeMatch` 中的 `XAddrs`（取第一个 http 地址）与 `Scopes`（提取 `onvif://www.onvif.org/name/` 作为设备名）。
- 按 `ServiceUrl` 去重，超时时间默认 3 秒（期间以 500ms 间隔轮询接收）。

## 依赖关系

- `OnvifClient` / `OnvifDiscovery` 位于 `Apiext.MediaKit.Server.Onvif` 命名空间，被 `Services/OnvifService.cs` 编排调用，
  页面入口为设备列表（`/devices`）的 ONVIF 详情抽屉 `Components/DeviceDetail/OnvifDetail.razor`。
- 设备记录实体为 `Persistence/OnvifDevice.cs`。
