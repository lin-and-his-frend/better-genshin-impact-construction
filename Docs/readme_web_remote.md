# BetterGI Web 远程控制与 API 说明

本文档介绍 BetterGI 的 Web 远程控制界面与 HTTP API（含集群群控 API）。

## 1. 功能范围
- Web UI：尽量对齐桌面端完整功能。
- 不包含：快捷键配置、辅助操控类功能。
- 屏幕传输与遮罩日志可在 Web 端查看（需在本地设置中开启）。

## 2. 启用与访问
### 2.1 本地启用
路径：**设置 → Web 远程控制**
- **启用 Web 远程控制**
- **监听端口**：默认 `50000`
- **允许局域网访问**：开启后监听所有网卡

### 2.2 访问地址
- 本机：`http://127.0.0.1:<端口>/`
- 局域网：`http://<本机局域网IP>:<端口>/`

如果手机/局域网无法访问：
- 尝试 **以管理员运行** 软件
- 或执行 URL ACL（示例，端口替换为实际端口）：
  ```powershell
  netsh http add urlacl url=http://+:50000/ user=Everyone
  ```
- 确认防火墙放行对应端口

## 3. 鉴权（Web 端）
- 在本地设置中开启“**启用鉴权**”。
- 账号/密码仅可在本地软件设置。
- 启用后所有 Web API 需要 HTTP Basic Auth。

## 4. 集群群控 API
### 4.1 开启与 Token
路径：**设置 → Web 远程控制 → 启用集群群控 API**
- 开启后会自动生成 **Token**
- 可点击“**生成**”按钮刷新 Token

### 4.2 白名单 IP
- 支持单个 IP 或 CIDR 网段（如 `192.168.1.0/24`）
- 多个条目可用 **逗号/换行**分隔
- 留空表示不限制

### 4.3 集群 API 入口
- **集群 API 仅通过 `/api/cluster/...` 访问**
- 任何集群请求必须携带 Token，否则 403
- 请求头之一即可：
  - `X-BGI-Cluster-Token: <token>`
  - `X-BGI-Token: <token>`
  - `Authorization: Bearer <token>`

> 集群 API 与普通 API 共享同一功能集合，只是路径加了 `/api/cluster` 前缀。

## 5. API 概览
### 5.1 Base URL
- 普通 API：`http://<host>:<port>/api`
- 集群 API：`http://<host>:<port>/api/cluster`

### 5.2 GET
- `/`：Web UI 页面
- `/api/status`：运行状态
- `/api/network`：本机局域网 IP
- `/api/strategies/auto-fight`
- `/api/strategies/tcg`
- `/api/options/domain-names`
- `/api/options/grid-names`
- `/api/options/fishing-time-policy`
- `/api/options/recognition-failure-policy`
- `/api/config/basic`：基础功能开关
- `/api/settings/auto-gi`
- `/api/settings/auto-wood`
- `/api/settings/auto-fight`
- `/api/settings/auto-domain`
- `/api/settings/auto-stygian`
- `/api/settings/auto-fishing`
- `/api/settings/auto-music`
- `/api/settings/auto-artifact`
- `/api/settings/grid-icons`
- `/api/one-dragon/configs`
- `/api/one-dragon/config?name=...`
- `/api/logs`：遮罩日志快照
- `/api/logs/stream`：SSE 日志流
- `/api/screen`：屏幕画面（PNG / SVG 占位提示）
- `/api/scripts/groups`
- `/api/scripts/group/detail?name=...`
- `/api/scripts/library`
- `/api/game/start`
- `/api/game/stop`

### 5.3 POST
- `/api/config/basic`
- `/api/settings/auto-gi`
- `/api/settings/auto-wood`
- `/api/settings/auto-fight`
- `/api/settings/auto-domain`
- `/api/settings/auto-stygian`
- `/api/settings/auto-fishing`
- `/api/settings/auto-music`
- `/api/settings/auto-artifact`
- `/api/settings/grid-icons`
- `/api/tasks/run`
- `/api/one-dragon/config`
- `/api/one-dragon/select`
- `/api/one-dragon/run`
- `/api/scripts/run`
- `/api/scripts/group/create`
- `/api/scripts/group/delete`
- `/api/scripts/group/rename`
- `/api/scripts/group/add-items`
- `/api/scripts/group/remove-item`
- `/api/scripts/group/update-item`
- `/api/scripts/group/reorder`
- `/api/tasks/cancel`
- `/api/tasks/pause`
- `/api/tasks/resume`

## 6. 关键说明
### 6.1 /api/screen
- 需要在本地设置中开启“**传输屏幕显示内容**”
- 游戏未启动/截图器未就绪时，会返回 SVG 提示图

### 6.2 /api/logs 与 /api/logs/stream
- 需要开启“**Web 端显示遮罩日志**”
- `/api/logs/stream` 为 **text/event-stream** SSE

### 6.3 /api/tasks/run 任务名
`task` 可用值（示例）：
- `auto_gi`
- `auto_wood`
- `auto_fight`
- `auto_domain`
- `auto_stygian`
- `auto_music`
- `auto_album`
- `auto_fishing`
- `auto_artifact_salvage`
- `get_grid_icons`
- `grid_icons_accuracy`
- `auto_redeem_code`

`params` 取值与各任务参数一致，可参考对应 `/api/settings/*` 返回内容。

## 7. 示例
### 7.1 普通 API（Basic Auth）
```bash
# 获取状态
curl -u admin:password http://127.0.0.1:50000/api/status
```

### 7.2 集群 API
```bash
# 获取状态（集群）
curl -H "X-BGI-Cluster-Token: <token>" http://192.168.1.10:50000/api/cluster/status
```

### 7.3 启动一条龙
```bash
curl -X POST \
  -H "Content-Type: application/json" \
  -H "X-BGI-Cluster-Token: <token>" \
  -d '{"name":"默认配置"}' \
  http://192.168.1.10:50000/api/cluster/one-dragon/run
```

---
如需扩展/新增接口，请以 `BetterGenshinImpact/Service/Remote/WebRemoteService.cs` 为准。
