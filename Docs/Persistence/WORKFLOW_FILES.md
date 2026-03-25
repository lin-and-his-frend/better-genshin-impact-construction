# 工作流与脚本文件说明

## 文件化范围

以下内容保持文件化，不纳入业务数据库正文：

- `User\Scripts\Js`
- `User\Scripts\KeyMouse`
- `User\Scripts\Combat`
- `User\Scripts\Tcg`
- `User\Scripts\Pathing`
- `User\Workflows\ScriptGroup`
- `User\Workflows\OneDragon`

## 原因

1. 这些文件是用户创作内容或工作流文档。
2. 用户需要手工编辑、复制、导入导出。
3. 文本 diff 与版本管理对这些内容更友好。

## 写入要求

1. 统一使用文件服务。
2. 统一做路径越界校验。
3. 统一使用原子写入。
4. 不再通过 `UserCache` 回灌数据库。

## 数据库只保留的关联元数据

- 当前选中的一条龙配置名
- 当前订阅的仓库和条目
- 运行时引用到的工作流选择状态
