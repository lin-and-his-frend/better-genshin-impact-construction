# 数据库 Schema 说明

## 总览

数据库文件：
- `User\config.db`

当前设计分为 4 类表：

1. schema / migration 元数据
2. 应用配置分表
3. 运行时业务记录
4. 仓库与资源元数据

## 元数据表

### `schema_meta`

用途：
- 保存 schema 版本
- 保存迁移来源
- 保存迁移完成时间

字段：
- `meta_key TEXT PRIMARY KEY`
- `meta_value TEXT NOT NULL`

## 配置主表

### `app_profile`

用途：
- 保存配置档案

字段：
- `profile_id INTEGER PRIMARY KEY`
- `profile_name TEXT NOT NULL`
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

备注：
- 当前默认只使用 `profile_id = 1`
- 结构上保留未来多配置档能力

## 配置分表

所有配置分表使用统一结构：

- `profile_id INTEGER PRIMARY KEY`
- `payload_json TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

统一约束：
- `profile_id` 外键关联 `app_profile(profile_id)`
- 当前只写 `profile_id = 1`
- 所有写入必须在同一事务里提交

### 已规划/已实现表

- `app_setting_core`
- `app_setting_mask_window`
- `app_setting_common`
- `app_setting_web_remote`
- `app_setting_mcp`
- `app_setting_ai`
- `app_setting_genshin_start`
- `app_setting_auto_pick`
- `app_setting_auto_skip`
- `app_setting_auto_fishing`
- `app_setting_quick_teleport`
- `app_setting_auto_cook`
- `app_setting_auto_genius_invokation`
- `app_setting_auto_wood`
- `app_setting_auto_fight`
- `app_setting_auto_music_game`
- `app_setting_auto_domain`
- `app_setting_auto_stygian_onslaught`
- `app_setting_auto_artifact_salvage`
- `app_setting_auto_eat`
- `app_setting_auto_leyline_outcrop`
- `app_setting_map_mask`
- `app_setting_skill_cd`
- `app_setting_auto_redeem_code`
- `app_setting_get_grid_icons`
- `app_setting_macro`
- `app_setting_record`
- `app_setting_script`
- `app_setting_pathing_condition`
- `app_setting_hot_key`
- `app_setting_notification`
- `app_setting_key_bindings`
- `app_setting_other`
- `app_setting_tp`
- `app_setting_dev`
- `app_setting_hardware_acceleration`

## 运行时业务记录表

### `runtime_execution_record`

用途：
- 任务执行记录
- 跳过策略判断数据源

建议字段：
- `record_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `group_name TEXT NOT NULL`
- `folder_name TEXT NOT NULL`
- `project_name TEXT NOT NULL`
- `project_type TEXT NOT NULL`
- `is_successful INTEGER NOT NULL`
- `start_time_local TEXT NOT NULL`
- `end_time_local TEXT`
- `start_time_server TEXT`
- `end_time_server TEXT`
- `created_utc TEXT NOT NULL`

索引建议：
- `(project_type, project_name, is_successful, end_time_local)`
- `(group_name, folder_name, project_name)`

### `runtime_task_progress_session`

用途：
- 任务恢复会话主记录

### `runtime_task_progress_group`

用途：
- 会话关联的脚本组

### `runtime_task_progress_history`

用途：
- 历史执行步骤

### `runtime_farming_daily`

用途：
- 每日锄地聚合状态

### `runtime_farming_record`

用途：
- 单次锄地记录

### `runtime_farming_remote_snapshot`

用途：
- 米游社同步后的聚合快照

## 仓库与资源元数据表

### `repo_source`

用途：
- 仓库来源配置

### `repo_subscription`

用途：
- 当前仓库订阅元数据

### `repo_subscription_item`

用途：
- 每个订阅条目明细

### `resource_pack_index`

用途：
- 资源包元数据，不存正文

### `rule_pack_index`

用途：
- 规则包元数据，不存正文

## 设计说明

### 为什么配置分表仍保留 `payload_json`

本期目标优先解决的是：
- 单一真源
- 明确分表
- 事务写入
- 旧双写退役

因此配置采用“按聚合分表 + 每表单份 JSON 载荷”的折中方案，而不是一次性把全部配置字段拆成数百个列。这比旧的 `main + split blob` 双写方案更稳定，也为后续逐表字段化提供了清晰边界。

### 为什么运行时记录尽量字段化

运行时业务记录需要：
- 查询
- 条件过滤
- 排序
- 统计

因此执行记录、任务进度、锄地统计应优先字段化，而不是继续保存整段 JSON 文件。
