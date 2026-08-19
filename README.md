# OpenCVCameraTracking

基于 `.NET 8 + WPF + OpenCvSharp` 的实时摄像头组件。核心采集、检测和跟踪逻辑位于
`OpenCVCameraTracking.Core`，WPF 界面与设置管理位于 `OpenCVCameraTracking`。

## 功能

- 枚举并打开 Windows USB/内置摄像头。
- 播放 RTSP、HTTP 视频流和本地视频文件。
- RTSP/TCP 打开与读取超时、断线重连。
- RTSP 低延迟模式：采集和推理解耦，始终处理最新帧，主动丢弃积压旧帧。
- 默认使用 OpenCV YuNet ONNX 进行人脸检测，保留 Haar 兼容模式。
- 内置 YOLOX INT8 ONNX 动物检测模型，无需另外下载模型即可使用。
- 支持自定义 YOLOv5/YOLOv8 ONNX 模型。
- IoU 多目标关联、位置平滑和短时丢失保留，画面中显示稳定目标编号。
- 设置窗口可管理多个网络视频流并记住默认选择。
- 保存来源类型、检测模式、模型、检测阈值、语言和上次输入的网络地址。
- 简体中文与 English 运行时切换。
- 自定义深色 ComboBox、ComboBoxItem、按钮、输入框和列表样式。

## 运行

要求：Windows 10/11、x64、.NET 8 SDK 或更高版本。

```powershell
dotnet restore OpenCVCameraTracking.slnx
dotnet run --project src/OpenCVCameraTracking/OpenCVCameraTracking.csproj
```

也可以在 Visual Studio 中打开 `OpenCVCameraTracking.slnx`，将 `OpenCVCameraTracking` 设为启动项目。

## 人脸检测

默认的“人脸（YuNet，推荐）”比旧 Haar 模型更适合以下场景：

- RTSP 高清画面中的相对较小人脸；
- 佩戴眼镜；
- 轻度侧脸或姿态变化；
- 光照变化。

设置窗口可调整人脸置信度。数值降低会更灵敏，但误检可能增加；默认值为 `0.55`。

“人脸（Haar，兼容）”仍可选择，主要用于不希望执行 ONNX DNN 的兼容场景。

## 动物检测

选择“动物（ONNX）”后可选择：

1. `内置 YOLOX INT8（COCO 动物）`：默认可用，无需浏览模型文件。
2. `自定义 YOLOv5 / YOLOv8 ONNX`：选择自己的 ONNX 文件。

内置模型过滤以下 COCO 类别：

```text
bird, cat, dog, horse, sheep, cow, elephant, bear, zebra, giraffe
```

内置模型文件约 8.7 MB，来源于 OpenCV Zoo。动物置信度默认是 `0.35`，可在设置窗口调整。

自定义模型支持常见输出：

- YOLOv5：`[1, 25200, 85]`
- YOLOv8：`[1, 84, 8400]`
- 带 NMS：`[1, N, 6]`

## 保存网络视频流

点击主窗口的“设置”：

1. 在“网络视频流”区域填写名称和完整地址。
2. 点击“添加 / 更新”。
3. 在“默认视频源”中选择需要默认使用的项目。
4. 点击“保存”。

重新启动后，主窗口“已保存的视频源”下拉框会恢复该选择及地址。切换主窗口中的已保存视频源时，
当前选择也会立即保存。

配置文件位置：

```text
%LocalAppData%\OpenCVCameraTracking\settings.json
```

注意：网络地址会按原文保存在当前 Windows 用户的本地配置中。如果 URL 包含摄像头用户名和密码，
请限制该配置文件的访问权限；生产环境可进一步将凭据迁移到 Windows Credential Manager。

## RTSP 低延迟

低延迟模式默认开启，包含：

- FFmpeg `rtsp_transport=tcp`；
- `nobuffer` 与 `low_delay`；
- 视频缓冲区请求为 1；
- 独立采集线程持续读取流；
- 推理线程只取最新的一帧。

因此即使动物 ONNX 推理速度低于摄像头帧率，画面也不会因为排队处理旧帧而不断增加延迟。
少数摄像头固件若不兼容低延迟 FFmpeg 参数，可在设置窗口关闭低延迟模式。

如果地址省略协议，例如：

```text
user:password@192.168.1.10:554/stream1
```

程序会自动补为：

```text
rtsp://user:password@192.168.1.10:554/stream1
```

## 多语言

设置窗口支持：

- 简体中文 `zh-CN`
- English `en-US`

保存后立即切换，不需要重新启动。资源位于：

```text
src/OpenCVCameraTracking/Languages/
```

增加语言时复制任意现有 `Strings.*.xaml`，翻译值并在 `LocalizationManager` 与语言下拉框中注册语言代码。

## 集成到现有 WPF 项目

引用 `OpenCVCameraTracking.Core`，然后创建检测器和引擎：

```csharp
var detector = new YuNetFaceDetector(
    "face_detection_yunet_2023mar.onnx",
    confidenceThreshold: 0.55f);

var tracker = new IouMultiObjectTracker(
    minimumIou: 0.18f,
    maximumMisses: 12,
    smoothing: 0.72f);

var engine = new CameraTrackingEngine(detector, detectionInterval: 1, tracker);

engine.FrameReady += (_, frame) =>
{
    // frame.Pixels: BGRA32
    // frame.Objects: 目标 ID、边框、类别、置信度
};

await engine.StartAsync(new CameraSourceOptions
{
    Kind = CameraSourceKind.Stream,
    Address = "rtsp://user:password@camera/stream",
    PreferTcpForRtsp = true,
    LowLatencyMode = true
});
```

窗口关闭或切换视频源时：

```csharp
await engine.DisposeAsync();
```

## 主要代码

- `Camera/DirectShowCameraEnumerator.cs`：Windows 视频设备枚举。
- `CameraTrackingEngine.cs`：视频采集、最新帧缓冲、RTSP 重连、绘制与帧事件。
- `Detection/YuNetFaceDetector.cs`：默认 YuNet 人脸检测。
- `Detection/HaarFaceDetector.cs`：Haar 兼容检测。
- `Detection/YoloXOnnxDetector.cs`：内置动物模型解析。
- `Detection/YoloOnnxDetector.cs`：自定义 YOLOv5/YOLOv8 模型解析。
- `Tracking/IouMultiObjectTracker.cs`：目标关联、编号和边框平滑。
- `Configuration/SettingsStore.cs`：JSON 设置持久化。
- `SettingsWindow.xaml`：语言、阈值和网络流管理。
- `Themes/Controls.xaml`：下拉框等控件模板。

## 模型来源

- YuNet：<https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet>
- YOLOX：<https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_yolox>
- Haar cascade：<https://github.com/opencv/opencv/tree/4.x/data/haarcascades>

模型目录同时包含对应许可证文本。

## 验证

已执行：

```text
dotnet build OpenCVCameraTracking.slnx -c Release
0 个警告，0 个错误
```

并完成：

- YuNet 对用户提供的 RTSP 截图执行真实推理，检测到 1 张人脸；
- 内置 YOLOX INT8 完成真实 ONNX 前向推理与输出解析；
- 主窗口和设置窗口启动烟雾测试；
- `dotnet format --verify-no-changes` 格式检查。
