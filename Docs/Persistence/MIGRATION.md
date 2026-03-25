# 持久化迁移说明

## 迁移目标

从以下旧来源迁移到新体系：

1. 旧 `config.db` 中的 `app_config` / `config_*` JSON blob
2. 旧 `user_files` 文件映射
3. `User\config.json`
4. `log\ExecutionRecords\*.json`
5. `log\task_progress\*.json`
6. `log\FarmingPlan\*.json`
7. 旧用户规则与资源文件路径

## 迁移顺序

1. 创建 `schema_meta` 和所有新表。
2. 读取旧主配置。
3. 写入新配置分表。
4. 迁移运行时业务记录。
5. 迁移仓库订阅元数据。
6. 调整文件目录布局。
7. 写入迁移标记。

## 配置迁移来源优先级

1. 旧 `app_config` 主配置
2. 旧 `config_*` 分桶 JSON
3. 旧 `user_files` 中的 `config.json`
4. 磁盘 `User\config.json`

## 文件迁移规则

### 保持文件化并搬迁目录

- `User\JsScript` -> `User\Scripts\Js`
- `User\KeyMouseScript` -> `User\Scripts\KeyMouse`
- `User\AutoFight` -> `User\Scripts\Combat`
- `User\AutoGeniusInvokation` -> `User\Scripts\Tcg`
- `User\AutoPathing` -> `User\Scripts\Pathing`
- `User\ScriptGroup` -> `User\Workflows\ScriptGroup`
- `User\OneDragon` -> `User\Workflows\OneDragon`
- `User\I18n` -> `User\ResourcePacks\I18n`
- `User\Images` -> `User\Resources\Images`

### 规则迁移

- `pick_black_lists.txt` -> `User\Rules\Pick\blacklist.txt`
- `pick_fuzzy_black_lists.txt` -> `User\Rules\Pick\fuzzy_blacklist.txt`
- `pick_white_lists.txt` -> `User\Rules\Pick\whitelist.txt`
- `pick_black_lists.json` / `pick_white_lists.json` -> 迁移后废弃

## 兼容策略

### 一次启动兼容

- 新逻辑优先读新表
- 新表不存在时，尝试执行一次迁移
- 迁移失败时，回退旧读取逻辑，但不再写回旧结构

### 一版本兼容

- 保留旧目录只读导入
- 不再向旧目录写入新业务状态

## 回滚原则

1. 迁移必须幂等。
2. 配置写入必须事务化。
3. 运行时记录迁移失败不能破坏旧文件。
4. 新文件目录切换前，保留旧路径备份。
