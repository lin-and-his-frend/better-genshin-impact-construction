# 持久化设计总览

本文档描述 BetterGI 当前版本的持久化边界、目录规划与维护规则。

## 设计目标

1. 结构化配置和运行时业务状态直接写入 SQLite，不再经过 `UserCache` 镜像回灌。
2. 用户创作内容、规则文本、资源包继续保持文件化，方便手工编辑与导入导出。
3. 日志与缓存统一归档到 `User\Log` 与 `User\Cache`，不再污染安装目录。
4. 外部系统状态明确排除出业务数据库，不再混淆 source of truth。

## 四层持久化模型

### 1. Database

位置：
- `User\config.db`

用途：
- 结构化应用设置
- 运行时业务记录
- 仓库订阅元数据
- 迁移与 schema 元数据

约束：
- 直接写库
- 单一真源
- 事务提交

### 2. User Files

位置：
- `User\Scripts`
- `User\Workflows`
- `User\Rules`
- `User\ResourcePacks`
- `User\Resources`

用途：
- 用户脚本
- 工作流文档
- 黑白名单规则
- 语言包
- 自定义图片

约束：
- 文件必须原子写入
- 不允许再自动镜像进 `config.db`

### 3. Cache / Log

位置：
- `User\Cache`
- `User\Log`

用途：
- 仓库克隆缓存
- 远程数据缓存
- 推理缓存
- 识别/调试输出
- 应用日志

约束：
- 可删除、可重建
- 不参与业务真源判定

### 4. External State

来源：
- Windows 注册表
- 原神安装目录与日志
- 启动器配置
- 系统驱动与推理环境

约束：
- 只读探测或显式系统集成操作
- 不纳入业务数据库

## 目录规划

```text
User/
  config.db
  Cache/
    ScriptRepo/
    Remote/
    ModelRuntime/
    VisionFeatures/
    MemoryFileCache/
  Log/
    App/
    Diagnostics/
    Exports/
  Scripts/
    Js/
    KeyMouse/
    Combat/
    Tcg/
    Pathing/
  Workflows/
    ScriptGroup/
    OneDragon/
  Rules/
    Pick/
      blacklist.txt
      fuzzy_blacklist.txt
      whitelist.txt
  ResourcePacks/
    I18n/
    Pick/
  Resources/
    Images/
```

## 维护铁律

1. 不允许新增“任意 `User\...` 文件自动写入数据库”的逻辑。
2. 不允许业务状态再写入 `log\*.json` 作为唯一真源。
3. 不允许结构化配置继续整棵 `AllConfig` 双写到旧 JSON blob 表。
4. 不允许把缓存和日志重新写回安装目录。
5. 不允许把 Windows / 游戏注册表状态当作 BetterGI 自身配置真源。

## 相关文档

- [SCHEMA.md](D:/Users/admin/Desktop/bgi项目/原版/better-genshin-impact-construction/Docs/Persistence/SCHEMA.md)
- [MIGRATION.md](D:/Users/admin/Desktop/bgi项目/原版/better-genshin-impact-construction/Docs/Persistence/MIGRATION.md)
- [STORAGE_MAP.md](D:/Users/admin/Desktop/bgi项目/原版/better-genshin-impact-construction/Docs/Persistence/STORAGE_MAP.md)
- [WORKFLOW_FILES.md](D:/Users/admin/Desktop/bgi项目/原版/better-genshin-impact-construction/Docs/Persistence/WORKFLOW_FILES.md)
- [RULE_PACKS.md](D:/Users/admin/Desktop/bgi项目/原版/better-genshin-impact-construction/Docs/Persistence/RULE_PACKS.md)
- [EXTERNAL_STATE.md](D:/Users/admin/Desktop/bgi项目/原版/better-genshin-impact-construction/Docs/Persistence/EXTERNAL_STATE.md)
