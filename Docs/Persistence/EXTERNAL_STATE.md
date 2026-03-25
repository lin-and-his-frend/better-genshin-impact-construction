# 外部系统状态说明

## 定义

外部系统状态指：
- 不由 BetterGI 负责生成和拥有
- BetterGI 只能读取，或在极少数场景下显式写入
- 不应纳入业务数据库作为 source of truth

## 清单

### 原神注册表设置

包括：
- 游戏语言
- 分辨率
- 输入设置
- `GENERAL_DATA`

处理方式：
- 只读探测
- 校验提示

### 启动器与安装路径

包括：
- HYP 注册表
- 卸载信息
- `config.ini`
- `output_log.txt`

处理方式：
- 统一外部探测服务读取

### Windows 协议注册

包括：
- `BetterGI` URL 协议

处理方式：
- 视为系统集成状态
- 不写入业务数据库

### DirectX 兼容开关

包括：
- `DirectXUserGlobalSettings`

处理方式：
- 视为系统级副作用
- 必须在文档和 UI 中明确提示

### 推理环境与系统驱动

包括：
- CUDA
- OpenVINO
- TensorRT
- GPU 驱动探测

处理方式：
- 仅探测
- 不作为业务配置真源
