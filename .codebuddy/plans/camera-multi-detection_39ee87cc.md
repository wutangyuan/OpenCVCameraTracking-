---
name: camera-multi-detection
overview: 为 CameraTrackingEngine 添加多检测器支持（CompositeObjectDetector），并新增"人+动物"检测模式：YuNet 人脸与 YOLOX 动物检测并发运行、结果合并展示。
todos:
  - id: composite-detector
    content: 创建 CompositeObjectDetector 复合检测器，并发执行子检测器并按标签合并去重
    status: completed
  - id: engine-multi-detector
    content: 扩展 CameraTrackingEngine 多检测器构造函数，用 [skill:lsp-code-analysis] 核对调用点兼容
    status: completed
    dependencies:
      - composite-detector
  - id: mainwindow-mode
    content: 重构 CreateDetector 抽取动物检测器构建，新增"人+动物"分支与面板逻辑
    status: completed
    dependencies:
      - engine-multi-detector
  - id: ui-resources
    content: MainWindow.xaml 新增"人+动物"下拉项并补充中英文语言资源
    status: completed
  - id: docs-verify
    content: 更新 README 说明，运行 dotnet build 与格式检查验证
    status: completed
    dependencies:
      - ui-resources
      - mainwindow-mode
---

## 用户需求
CameraTrackingEngine 添加支持多种检测，可以同时支持检测人和动物。经确认，人员检测采用 YuNet 人脸检测，动物检测采用内置 YOLOX INT8 模型，两个检测器并发运行，全部使用内置模型，无需额外下载。

## 产品概述
在现有 WPF + OpenCvSharp 摄像头跟踪应用（检测模式为人脸 YuNet / 人脸 Haar / 动物 ONNX）基础上，新增第四种"人 + 动物"复合检测模式，使同一画面中可同时标出人员（face）与动物（10 类 COCO 动物），原有三种模式保持不变。

## 核心功能
- 新增复合检测器：聚合多个子检测器，同一帧内并发执行推理并合并检测结果。
- 新增"人 + 动物"检测模式：YuNet 检测人脸（标注 face），内置 YOLOX INT8 或自定义 YOLO 检测动物，两类目标同时显示。
- 引擎支持多检测器：CameraTrackingEngine 可直接接收多个检测器，自动组合执行，保持现有单检测器用法兼容。
- 结果合并与去重：跨检测器按类别标签对重叠框做 NMS 去重；跟踪器按标签关联，"face" 与动物标签天然隔离互不干扰。
- UI 更新：主窗口检测模式下拉框新增"人 + 动物"选项，复用现有动物模型选择面板（内置/自定义）。
- 设置持久化：检测模式以字符串 Tag 保存，无需新增配置字段，重启后自动恢复。


## 技术选型
- 复用现有技术栈：.NET 8 + WPF + OpenCvSharp，无新技术引入。
- 新增 `CompositeObjectDetector` 实现现有 `IObjectDetector` 接口（组合模式），不改变接口契约。

## 实现方案
### 核心策略
1. **复合检测器**：在 `Core/Detection/` 下新增 `CompositeObjectDetector`，构造时接收 `IEnumerable<IObjectDetector>`（至少 1 个，否则抛 `ArgumentException`），`Detect` 内通过 `Task.Run` + `Task.WhenAll` 并发执行各子检测器推理，再合并结果。
2. **合并去重**：合并后按 Label（OrdinalIgnoreCase）分组，对同标签重叠框执行 `CvDnn.NMSBoxes` 去重，防止未来组合同类模型时出现重复框；单检测器时直接返回其结果，避免 Task 开销。
3. **引擎扩展**：`CameraTrackingEngine` 新增 `IEnumerable<IObjectDetector>` 构造函数重载，内部包装为 `CompositeObjectDetector` 存入现有 `_detector` 字段；保留原单检测器构造函数，`DisposeAsync` 经复合检测器级联释放所有子检测器，零破坏性。
4. **跟踪器零改动**：`IouMultiObjectTracker.Update` 按 Label 关联，"face" 与动物标签互不干扰，复合结果可直接喂入。
5. **UI 复用与重构**：抽取 `CreateAnimalDetector()` 私有方法（内置 YOLOX / 自定义 YOLO 构建逻辑），供 "Animal" 与 "PersonAnimal" 两个分支复用；`UpdateModelPanels` 在两种模式下均显示动物模型面板；模式以 `Tag="PersonAnimal"` 持久化到现有 `SelectedDetectionMode` 字符串字段。

### 性能与可靠性
- 并发推理总耗时约等于最慢检测器（YOLOX INT8 640x640 为主瓶颈，YuNet 320x320 很轻量），引擎既有的 `detectionInterval` 隔帧检测机制天然适配，无需调整。
- 线程安全：各子检测器只读输入帧（内部 letterbox 至新 Mat 再推理），并发读安全；子检测器异常经 `Task.WhenAll` 聚合抛出，由引擎现有 `Faulted` 事件处理，不新增错误通道。
- 兼容性：保留单检测器构造与所有现有调用；不引入与需求无关的重构；设置文件向后兼容。

## 架构设计
```
MainWindow.CreateDetector()（Tag=PersonAnimal）
  └─ CompositeObjectDetector
       ├─ YuNetFaceDetector        (face，内置模型，置信度 FaceConfidence)
       └─ YoloXOnnxDetector / YoloOnnxDetector (动物，内置/自定义，置信度 AnimalConfidence)
            ↓ Detect 并发执行，按 Label 合并 + NMS 去重
       CameraTrackingEngine(IEnumerable<IObjectDetector>)
            ↓ _tracker.Update(detections)  按 Label 关联
       IouMultiObjectTracker → FrameReady 事件（绘制 face 与动物框）
```

## 目录结构
```
src/OpenCVCameraTracking.Core/
├── Detection/
│   └── CompositeObjectDetector.cs   # [NEW] 复合检测器。构造接收 IEnumerable<IObjectDetector>（空则抛 ArgumentException）；Detect 并发执行各子检测器并合并结果，按 Label 分组做 NMS 去重，单检测器时直接透传；Dispose 遍历释放全部子检测器，异常向上传播。
└── CameraTrackingEngine.cs          # [MODIFY] 新增 IEnumerable<IObjectDetector> 构造函数重载，内部用 CompositeObjectDetector 包装后赋给 _detector；其余逻辑（ProcessFrame、DisposeAsync 级联释放）不变。
src/OpenCVCameraTracking/
├── MainWindow.xaml                   # [MODIFY] DetectionModeBox 在 Animal 之后新增 ComboBoxItem，Content 绑定 PersonAnimalMode 资源，Tag="PersonAnimal"。
├── MainWindow.xaml.cs                # [MODIFY] CreateDetector 新增 "PersonAnimal" 分支（CompositeObjectDetector = YuNet + 动物检测器）；抽取 CreateAnimalDetector() 供 Animal/PersonAnimal 复用；UpdateModelPanels 在 "Animal" 或 "PersonAnimal" 时显示 ModelPanel。
└── Languages/
    ├── Strings.zh-CN.xaml            # [MODIFY] 新增键 PersonAnimalMode："人 + 动物（ONNX）"。
    └── Strings.en-US.xaml            # [MODIFY] 新增键 PersonAnimalMode："People + Animals (ONNX)"。
README.md                             # [MODIFY] 功能列表与检测章节补充"人 + 动物"复合检测说明。
```

## 关键代码结构
```csharp
// 复合检测器核心契约
public sealed class CompositeObjectDetector : IObjectDetector
{
    public CompositeObjectDetector(IEnumerable<IObjectDetector> detectors); // 空集合抛 ArgumentException
    public IReadOnlyList<Detection> Detect(Mat bgrFrame); // 并发推理 + 按 Label 合并去重
    public void Dispose(); // 释放所有子检测器
}

// 引擎新增重载（保留原单检测器构造函数）
public CameraTrackingEngine(
    IEnumerable<IObjectDetector> detectors,
    int detectionInterval = 2,
    IouMultiObjectTracker? tracker = null);
```


## Agent Extensions
### Skill
- **lsp-code-analysis**
  - 用途：实施阶段用于影响分析——确认 `CameraTrackingEngine` 构造函数、`CreateDetector`、`UpdateModelPanels` 的全部调用点与引用，确保新增重载与模式分支无遗漏、无回归。
  - 预期结果：修改覆盖完整，新增多检测器构造与"人+动物"分支不影响现有 Face/Haar/Animal 三种模式的构建与面板切换逻辑。
