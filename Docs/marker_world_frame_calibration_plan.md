# Marker 业务世界坐标系与机器人标定计划

## 1. 文档状态

- 状态：Marker 只读 telemetry 已实现；业务世界标定与场景对齐仍处于设计阶段
- 目标项目：XRoboToolkit Unity Client（当前 Android 应用名 `XR-NOP`）
- 目标设备：PICO 6DoF 企业版设备
- 核心方案：使用固定 Marker 作为稳定的业务世界坐标系，在每次 Tracking 会话开始时完成一次对齐，然后锁定本次会话的变换

本文定义标定方案、接口边界、验证方法和实施顺序。当前业务代码只负责注册
Marker 回调并记录原始、带版本的稀疏 telemetry；尚未据此修改场景根节点、
现有 Head/Controller 位姿数值或机器人坐标系。

## 2. 背景与目标

PICO 的 Tracking 世界坐标系适合表达同一会话内的头显和手柄位姿，但其原点及水平朝向可能在应用重启、头显重启、重新居中、Tracking 丢失或地图重建后发生变化。因此，不能在未经验证的情况下把 PICO Tracking 原点当作长期不变的机器人工作站原点。

计划在机器人底座或固定工作台上安装一个静态 Marker，并把 Marker 坐标系定义为业务世界坐标系。每次会话识别该 Marker 后，建立：

```text
PICO Tracking Space  <--本次会话对齐-->  Marker World  <--固定安装外参-->  Robot Base
```

目标如下：

1. Unity、PICO Head/Controller、机器人基座和业务对象使用明确且可追踪的坐标关系。
2. 每次会话只需短暂观察 Marker 完成对齐，不要求 Marker 始终可见。
3. Marker 检测抖动不直接驱动整个 Unity 场景逐帧晃动。
4. 区分可长期保存的机械安装外参和只对当前 Tracking 会话有效的对齐结果。
5. 为后续把 Marker 世界系写入 TCP/录制数据和 ROS 侧 frame 契约保留清晰接口。

## 3. 已确认的 SDK 能力

Unity Enterprise SDK 已提供以下接口：

```csharp
PXR_Enterprise.SetMarkerInfoCallback(
    TrackingOriginModeFlags trackingMode,
    float cameraYOffset,
    Action<List<MarkerInfo>> markerInfos);
```

接口返回值：

- `0`：回调注册成功
- `1`：回调注册失败

回调中的 `MarkerInfo` 包含：

| 字段 | 当前 SDK 定义 |
|---|---|
| `posX/posY/posZ` | Marker 位置 |
| `rotationX/Y/Z/W` | Marker 旋转四元数，Unity 构造顺序为 XYZW |
| `validFlag` | `0` 无效，`1` 有效 |
| `markerType` | `1` 静态，`0` 动态 |
| `iMarkerId` | Marker ID |
| `dTimestamp` | 检测图像时间戳 |
| `reserve` | 保留字段，当前不能赋予业务语义 |

SDK 的 `MarkerInfoCallback` 已对 AAR 原始结果进行 Unity 坐标转换，包括 Z 轴和四元数部分分量的符号转换，并根据 Tracking Origin 模式调整 Y。因此，业务层第一版应直接使用回调后的 `MarkerInfo`，不要再次手工翻转坐标轴。

相关但尚未确认是否为前置条件的企业设置：

- `SFS_TRACKING_ENABLE_DYNAMIC_MARKER`：动态 Marker 检测开关
- `SFS_RETRIEVE_MAP_BY_MARKER_FIRST`：地图重定位时优先使用 Marker

本方案优先使用 `markerType == 1` 的静态 Marker。以上两个开关与静态 Marker 回调之间的依赖关系必须通过 PICO 文档或真机实验确认，不能先假定必须开启。

## 4. 尚未确认的关键事实

以下内容不能仅凭当前 AAR/Unity 包装代码得出，必须在开发前或第一阶段真机验证：

1. PICO Marker 的图案类型、生成方式、物理尺寸和打印要求。
2. 是否兼容 ArUco、AprilTag 或其他公开 Marker 编码。
3. 回调位姿严格表示“Marker 在 Tracking 世界中”，还是相反方向。
4. 平移单位是否为米。
5. Marker 局部 X/Y/Z 轴相对于图案正面和边缘的方向。
6. `dTimestamp` 的单位、时钟域和是否单调递增。
7. 回调频率、检测距离、视角限制、遮挡恢复行为和最大同时识别数量。
8. 应用重启、头显重启、重新居中及 Tracking 恢复后，PICO Tracking 世界是否保持一致。
9. SDK 是否提供正式的回调注销接口；当前公开包装中未看到对应方法。

这些问题在确认前都应标记为“待验证”，避免形成错误的坐标或生命周期契约。

## 5. 坐标系与变换约定

统一采用以下符号：

```text
P：PICO/Unity Tracking 坐标系
M：固定 Marker 坐标系，同时作为业务世界坐标系
R：机器人基座坐标系
H：头显坐标系
X：任意业务对象坐标系
```

`T_A_B` 定义为：

> B 坐标系在 A 坐标系中的位姿；它把 B 中表达的点或位姿变换到 A 中。

即：

```text
p_A = T_A_B * p_B
```

预计 Marker 回调给出：

```text
T_P_M：Marker 在本次 PICO Tracking 世界中的位姿
```

该方向必须通过真机实验确认后才能进入正式实现。

### 5.1 将 PICO 位姿表达为 Marker 世界位姿

例如，PICO 返回头显位姿 `T_P_H`，则头显在 Marker 世界中的位姿为：

```text
T_M_H = inverse(T_P_M) * T_P_H
```

任意 PICO 世界对象同理：

```text
T_M_X = inverse(T_P_M) * T_P_X
```

### 5.2 将 Marker 世界内容放入 Unity/PICO 场景

如果 Unity 内容是在 Marker 局部坐标中建模，要将其渲染到 PICO Tracking 世界：

```text
T_P_X = T_P_M * T_M_X
```

因此，如果建立一个 `MarkerWorldRoot`，并让所有 Marker 世界内容成为它的子节点，则应该把根节点的世界位姿设置为 `T_P_M`：

```text
Unity Tracking World
└── MarkerWorldRoot              world pose = T_P_M
    ├── RobotRoot                local pose = T_M_R
    ├── Targets                  local poses expressed in M
    └── Diagnostics
```

注意：

- “把已有 PICO 位姿换算到 Marker 坐标”使用 `inverse(T_P_M)`。
- “把 Marker 坐标中的内容放到 PICO 世界”使用 `T_P_M`。
- 两者用途不同，不能因为看到逆矩阵就直接把 `MarkerWorldRoot` 设置成逆位姿。

### 5.3 机器人安装外参

若 Marker 与机器人基座不完全重合，需要测量并保存：

```text
T_M_R：机器人基座在 Marker 世界中的固定安装位姿
```

机器人末端或目标位姿 `T_R_X` 转到 Marker 世界：

```text
T_M_X = T_M_R * T_R_X
```

需要在 PICO Tracking 世界渲染时：

```text
T_P_X = T_P_M * T_M_R * T_R_X
```

如果 Marker 的原点和轴向经过机械安装后与机器人基座严格重合，则 `T_M_R = I`。在没有测量证据时不能直接假定为单位矩阵。

## 6. 持久化边界

需要区分两类数据。

### 6.1 可长期保存的数据

- 目标 `markerId`
- Marker 类型和物理规格
- `T_M_R`：Marker 到机器人基座的机械安装外参
- 外参版本、测量日期、测量方法和负责人
- 坐标轴定义、单位和四元数顺序

只要 Marker 或机器人底座没有被移动，这些数据可以跨会话使用。

### 6.2 默认只对当前会话有效的数据

- `T_P_M`：本次 PICO Tracking 世界到 Marker 世界的对齐结果
- 本次采样数量、位置/角度离散度和时间范围
- 设备序列号、系统版本、应用版本和 Tracking Origin 模式

可以把 `T_P_M` 保存下来用于诊断和回放，但第一版不得在下一次启动时无条件复用。只有在后续证明 PICO 地图身份和 Tracking 原点能够被可靠验证时，才能增加“复用上次会话对齐”的快速路径。

## 7. 推荐运行流程

### 7.1 状态机

```text
WaitingForService
    -> SearchingMarker
    -> CollectingSamples
    -> ValidatingCalibration
    -> Locked

任一步骤失败：Error 或回到 SearchingMarker
Locked 后用户主动操作：Recalibrating -> CollectingSamples
```

状态语义：

- `WaitingForService`：等待 `OnBindEnterpriseService(true)`。
- `SearchingMarker`：回调已经注册，但尚未收到目标 ID 的有效静态 Marker。
- `CollectingSamples`：只收集目标 ID 且 `validFlag == 1` 的样本。
- `ValidatingCalibration`：计算稳定位姿和质量指标。
- `Locked`：本次会话的 `T_P_M` 已固定；Marker 后续短时丢失不影响当前场景。
- `Recalibrating`：用户明确要求重新建立对齐，不应静默覆盖已锁定结果。

### 7.2 单次标定流程

1. Enterprise service 绑定成功后注册 Marker 回调。
2. 选择配置中的目标 `markerId`，默认拒绝其他 Marker。
3. 要求 Marker 固定不动，并收集连续有效样本。
4. 对输入做有限值、四元数模长、ID、类型和时间戳检查。
5. 剔除相对于窗口中位姿跳变过大的异常样本。
6. 对位置使用中位数或鲁棒均值。
7. 对四元数先统一符号，再使用适合旋转的平均方法；禁止直接分别平均四个分量后不归一化。
8. 计算位置标准差、最大位置残差、角度标准差和最大角度残差。
9. 质量达标后生成 `T_P_M`，设置 `MarkerWorldRoot` 并进入 `Locked`。
10. 锁定后停止处理新 Marker 样本，但保留可控的重新标定入口。

初始实验可从 30～100 个样本开始。位置和角度阈值必须根据真机静态噪声确定，不应在没有数据时把某个毫米或角度值写成最终标准。

## 8. 实施阶段

### 阶段 0：设备与 Marker 前置调查

工作项：

- 确认当前 Sparrow/PICO 系统版本是否支持该接口。
- 获取官方支持的 Marker 样例、编码规则和物理尺寸说明。
- 确认静态 Marker 是否需要系统设置或地图配置。
- 确认回调注册、重复注册和生命周期行为。

完成标准：设备能够稳定回调指定 Marker 的 ID、类型和原始位姿。

### 阶段 1：只读诊断原型

先实现独立诊断组件，不改变现有 Tracking/TCP 数据：

- 在 Enterprise service bind 后注册回调。
- 将原始回调 JSON、解析结果和返回码写入日志。
- 显示目标 ID、有效状态、时间戳、位置、旋转和回调频率。
- 记录静止、移动头显、移动 Marker、遮挡和重新出现的数据。

完成标准：通过实验确认单位、轴向、变换方向、回调频率和丢失语义。

### 阶段 2：标定核心与质量评估

新增与 UI/网络解耦的标定模块，职责包括：

- 状态机管理
- 目标 Marker 筛选
- 样本缓冲和异常值剔除
- 位置与旋转稳健估计
- 标定质量报告
- 锁定、失败和重新标定

数学部分应使用独立、可单元测试的纯 C# 代码，避免依赖场景对象。

完成标准：使用录制样本可重复得到一致结果，并能拒绝明显跳变或无效数据。

### 阶段 3：Unity 场景集成

- 在场景中引入唯一的 `MarkerWorldRoot`。
- 明确哪些机器人模型、目标点和可视化对象属于 Marker 世界并迁移到该根节点下。
- 相机/XR Origin 保持由 PICO Tracking 驱动，不把相机挂到 `MarkerWorldRoot` 下。
- 标定锁定后一次性设置 `MarkerWorldRoot` 的世界位姿。
- 加入“搜索中、采集中、已锁定、质量不足、重新标定”状态反馈。

完成标准：移动头显时 Marker 世界内容保持固定；Marker 离开视野后场景不跳变；主动重新标定才更新根节点。

### 阶段 4：配置与持久化

计划保存两份逻辑数据：

1. 站点配置：目标 Marker、`T_M_R` 和坐标契约。
2. 会话标定记录：`T_P_M`、质量指标和设备/软件信息，仅用于诊断或显式恢复。

建议的会话记录结构：

```json
{
  "schemaVersion": 1,
  "markerId": 0,
  "markerType": 1,
  "trackingOriginMode": "Floor",
  "cameraYOffsetMeters": 0.0,
  "picoFromMarker": {
    "positionMeters": [0.0, 0.0, 0.0],
    "rotationXyzw": [0.0, 0.0, 0.0, 1.0]
  },
  "markerFromRobot": {
    "positionMeters": [0.0, 0.0, 0.0],
    "rotationXyzw": [0.0, 0.0, 0.0, 1.0]
  },
  "quality": {
    "acceptedSamples": 0,
    "rejectedSamples": 0,
    "positionStdMeters": 0.0,
    "rotationStdDegrees": 0.0
  }
}
```

最终 schema 中还应加入 UTC 时间、设备标识、PICO OS/SDK 版本和应用版本。矩阵方向必须同时通过字段名和文档说明，避免使用含糊的 `extrinsic` 单字段名。

### 阶段 5：Tracking、TCP 与 ROS 坐标契约

在 Marker 标定可靠前，不改动现有高频 Enterprise tracking 管线。完成前四阶段后再决定网络策略：

- 方案 A：保持现有 PICO Tracking 位姿不变，随数据发送 `T_M_P = inverse(T_P_M)` 和 frame 元数据，由 PC 转换。
- 方案 B：Unity 发送前把 Head/Controller 位姿统一换算到 Marker 世界，并明确标记 `frame_id=marker_world`。
- 方案 C：同时保留原始位姿和 Marker 世界位姿，便于调试但增加协议负担。

原则：一条 pose 数据必须明确说明其所属 frame；不能只变换数值而沿用旧协议的隐式坐标含义。该阶段需要与 xctrl-ros2 现有 pose frame 契约一起评审。

### 阶段 6：验收与回归

至少完成以下真机测试：

| 测试 | 操作 | 预期结果 |
|---|---|---|
| 静态噪声 | 头显和 Marker 均静止 | 输出质量指标稳定，满足选定阈值 |
| 头显绕行 | Marker 固定，头显从不同角度观察 | 估计的 `T_P_M` 在同一会话内基本一致 |
| Marker 移动 | 人为平移/旋转 Marker | 回调变化方向与已知动作一致，验证轴向和变换方向 |
| 遮挡 | 锁定后遮挡 Marker | 业务世界不跳变、不归零 |
| 重新标定 | 移动 Marker 后主动重标定 | 只有确认重标定后才更新世界对齐 |
| App 重启 | 保持物理布置，重启 App | 重新识别 Marker 后恢复同一业务世界 |
| 头显重启 | 重启设备后重新标定 | 不依赖旧 `T_P_M`，重新恢复业务世界 |
| Recenter | 执行系统重新居中 | 检测会话外参失效或要求重新标定，不静默使用错误外参 |
| Tracking 丢失 | 制造丢失并恢复 | 有明确状态和恢复策略，不产生大幅无提示跳变 |
| 多 Marker | 同时出现多个 ID | 只使用配置目标，结果不被其他 ID 替换 |

## 9. 安全与失败策略

- 未锁定 Marker 世界时，不把机器人高风险目标解释为已经完成世界对齐。
- 标定质量不足时保留上一个有效结果，但明确提示当前重新标定失败。
- 收到 `validFlag == 0`、NaN、无穷值、异常四元数或错误 ID 时必须拒绝样本。
- Marker 已锁定后，新的回调不得自动覆盖外参。
- 如果用户执行 Recenter、切换 Tracking Origin 或检测到 Tracking 空间重建，应把会话标定标记为可疑或失效。
- 网络消息和落盘数据必须携带 frame 名称与标定版本，避免 PC/ROS 端误用。
- 机器人真正执行动作前，后续系统应增加已知点或安全姿态的人工/自动复核环节。

## 10. 第一版明确不做的内容

- 不持续逐帧用 Marker 修正整个场景。
- 不自行实现 RGB 相机上的 ArUco/AprilTag 检测。
- 不假定 PICO Marker 与公开 Marker 标准兼容。
- 不自动估计 `T_M_R`；第一版使用经过测量或独立标定得到的安装外参。
- 不在尚未确认 frame 契约时直接改变现有 TCP tracking pose 数值。
- 不无条件跨头显重启复用上次 `T_P_M`。

## 11. 预期代码边界（后续实现参考）

命名可在开发时按项目风格调整，但职责建议保持分离：

```text
Assets/Scripts/MarkerCalibration/
├── PicoMarkerProvider.cs          PICO Enterprise 回调和生命周期
├── MarkerCalibrationService.cs   状态机、采样和锁定
├── MarkerPoseEstimator.cs        纯数学估计和质量计算
├── MarkerWorldRootController.cs  Unity 根节点应用
├── MarkerCalibrationStore.cs     配置和会话记录
└── MarkerCalibrationModels.cs    数据结构与 schema
```

不建议把这些职责直接堆入当前 `UIOperate.cs`。`UIOperate` 最多负责在 Enterprise service bind 事件发生时通知 Provider/Service。

## 12. 里程碑与通过条件

### M1：接口可用

- 真机收到目标 Marker 有效回调。
- 已确认 Marker 规格、单位、轴向和变换方向。

### M2：单次标定可靠

- 可以从一组样本得到稳定 `T_P_M`。
- 有可解释的质量指标和失败原因。
- 单元测试覆盖变换方向、逆变换、四元数符号和异常值。

### M3：Marker 世界可用

- `MarkerWorldRoot` 下的场景内容与物理 Marker 对齐。
- Marker 遮挡后保持锁定，主动重标定行为正确。

### M4：机器人对齐可用

- `T_M_R` 方向经过已知物理点验证。
- Unity 中机器人模型与真实机器人基座/工作台达到项目约定误差。
- 误差测量结果随标定文件存档。

### M5：数据链路契约完成

- TCP/录制/ROS 明确标注 pose frame 和标定版本。
- 原始 PICO Tracking 数据与 Marker 世界数据不会被混用。

## 13. 开发前需要做出的决策

开始编码前，应基于阶段 0 和阶段 1 的证据确认：

1. 使用的实际 Marker 类型、尺寸与目标 ID。
2. Marker 安装位置以及 M 坐标轴如何与机器人 R 坐标轴对应。
3. 第一版采用 `TrackingOriginModeFlags.Floor` 还是 `Device`；当前倾向 `Floor + cameraYOffset=0`。
4. 稳定样本窗口、最低持续时间和质量阈值。
5. `T_M_R` 的获取方式：机械测量、已知 CAD 关系或独立标定。
6. Marker 世界位姿由 Unity 转换还是 PC/ROS 转换。
7. Recenter 和 Tracking 恢复后采用自动失效还是人工确认策略。

只有在 M1 的坐标语义验证完成后，才进入会改变场景或网络数据的开发阶段。
