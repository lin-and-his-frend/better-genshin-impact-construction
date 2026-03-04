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
- **允许第三方访问**：开启后优先监听 `0.0.0.0` 与 `[::]`（IPv6），并回退到通配监听

### 2.2 访问地址
- 本机：`http://127.0.0.1:<端口>/`
- 第三方设备：`http://<本机IP>:<端口>/`

如果第三方设备无法访问：
- 尝试 **以管理员运行** 软件
- 或执行 URL ACL（示例，端口替换为实际端口）：
  ```powershell
  netsh http add urlacl url=http://+:50000/ user=Everyone
  ```
- 确认防火墙放行对应端口

## 3. 鉴权（Web 端）
- Web 鉴权已改为**强制启用**，不再提供鉴权开关。
- 必须在本地设置中配置账号/密码；若为空，Web 远程控制不会启动。
- Web 页面默认使用登录页 + 会话 Cookie 鉴权。
- 普通 API 兼容 HTTP Basic Auth（便于脚本/CLI 调用）。

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
- FastAPI 风格文档（集群视图）可用：
  - `/api/cluster/openapi.json`
  - `/api/cluster/docs`
  - `/api/cluster/redoc`
  - `/api/cluster/meta`（集群入口元信息）
  - `/api/cluster/routes`（可调用路由清单）
  - `/api/cluster/health`（健康检查）

> 集群 API 与普通 API 共享同一功能集合，只是路径加了 `/api/cluster` 前缀。

## 5. API 概览
### 5.1 Base URL
- 普通 API：`http://<host>:<port>/api`
- 集群 API：`http://<host>:<port>/api/cluster`

### 5.2 GET
- `/`：Web UI 页面
- `/login`：Web 登录页
- `/api-center` / `/api-center.html` / `/web/api-center.html`：独立接口中心页面（FastAPI 文档独立页）
- `/openapi.json`：FastAPI 兼容 OpenAPI Schema
- `/docs`：Swagger UI
- `/redoc`：ReDoc
- `/api/status`：运行状态
- `/api/health`：健康检查（轻量状态）
- `/api/network`：本机可访问 IP（IPv4/IPv6）
- `/api/auth/me`：当前登录状态
- `/api/meta`：API 入口元信息（含 docs/openapi 路径）
- `/api/routes`：可调用路由清单（与 `/api/ui/routes` 同源）
- `/api/openapi.json`：FastAPI 兼容 OpenAPI Schema（API 前缀版本）
- `/api/docs`：Swagger UI（API 前缀版本）
- `/api/redoc`：ReDoc（API 前缀版本）
- `/api/strategies/auto-fight`
- `/api/strategies/tcg`
- `/api/options/domain-names`
- `/api/options/grid-names`
- `/api/options/fishing-time-policy`
- `/api/options/recognition-failure-policy`
- `/api/options/leyline-types`
- `/api/options/leyline-countries`
- `/api/options/elite-drop-mode`
- `/api/options/sunday-values`
- `/api/options/resin-priority`
- `/api/options/script-project-statuses`
- `/api/options/script-project-schedules`
- `/api/options/notification-channels`
- `/api/config/basic`：基础功能开关
- `/api/config/get?path=...`：按路径读取配置（需开启高级配置接口）
- `/api/settings/auto-gi`
- `/api/settings/auto-wood`
- `/api/settings/auto-fight`
- `/api/settings/auto-domain`
- `/api/settings/auto-stygian`
- `/api/settings/auto-fishing`
- `/api/settings/auto-music`
- `/api/settings/auto-artifact`
- `/api/settings/grid-icons`
- `/api/settings/auto-leyline`
- `/api/settings/notification`
- `/api/one-dragon/configs`
- `/api/one-dragon/config?name=...`
- `/api/one-dragon/options`
- `/api/logs`：遮罩日志快照
- `/api/logs/stream`：SSE 日志流
- `/api/screen`：屏幕画面（PNG / SVG 占位提示）
- `/api/scripts/groups`
- `/api/scripts/group?name=...`
- `/api/scripts/group/detail?name=...`
- `/api/scripts/library`
- `/api/library/js`
- `/api/library/pathing`
- `/api/library/keymouse`
- `/api/ui/schema`：Web UI 动态页面描述
- `/api/ui/routes`：Web 可用路由清单
- `/api/ui/i18n?lang=...`：Web UI 语言词典（复用软件 `User/I18n/*.json`）

> `openapi.json/docs/redoc` 支持 `scope=cluster` 查询参数，可在普通 Web 登录态下直接查看 `/api/cluster/*` 路径规范（例如 `/docs?scope=cluster`）。
> `meta` 也支持 `scope=cluster`（例如 `/api/meta?scope=cluster`），用于在普通 Web 登录态下预览集群入口信息。
> `routes` 在集群 scope（`/api/cluster/routes` 或 `/api/routes?scope=cluster`）下会返回已带 `/api/cluster` 前缀的路由列表，便于第三方集群直接调用。

### 5.3 POST
- `/api/auth/login`
- `/api/auth/logout`
- `/api/config/basic`
- `/api/config/set`：按路径写配置（需开启高级配置接口）
- `/api/settings/auto-gi`
- `/api/settings/auto-wood`
- `/api/settings/auto-fight`
- `/api/settings/auto-domain`
- `/api/settings/auto-stygian`
- `/api/settings/auto-fishing`
- `/api/settings/auto-music`
- `/api/settings/auto-artifact`
- `/api/settings/grid-icons`
- `/api/settings/auto-leyline`
- `/api/settings/notification`
- `/api/notification/test`：测试指定通知通道（请求体示例：`{"channel":"webhook"}`）
- `/api/tasks/run`
- `/api/one-dragon/config`
- `/api/one-dragon/config/clone`
- `/api/one-dragon/config/rename`
- `/api/one-dragon/config/delete`
- `/api/one-dragon/select`
- `/api/one-dragon/run`
- `/api/game/start`
- `/api/game/stop`
- `/api/scripts/run`
- `/api/scripts/group/create`
- `/api/scripts/group/delete`
- `/api/scripts/group/rename`
- `/api/scripts/group/save`
- `/api/scripts/group/add-items`
- `/api/scripts/group/remove-item`
- `/api/scripts/group/update-item`
- `/api/scripts/group/batch-update`
- `/api/scripts/group/reorder`
- `/api/scripts/group/copy`
- `/api/scripts/group/reverse`
- `/api/scripts/group/set-next`
- `/api/scripts/group/set-next-group`
- `/api/scripts/group/run-from`
- `/api/scripts/group/export-merged`
- `/api/library/js/run`
- `/api/library/js/delete`
- `/api/library/pathing/run`
- `/api/library/pathing/delete`
- `/api/library/keymouse/play`
- `/api/library/keymouse/delete`
- `/api/library/keymouse/rename`
- `/api/library/keymouse/record/start`
- `/api/library/keymouse/record/stop`
- `/api/ai/chat`
- `/api/tasks/cancel`
- `/api/tasks/pause`
- `/api/tasks/resume`

### 5.4 Schema 驱动 Web UI
- Web 首页默认通过 `/api/ui/schema` 渲染页面、区块、表单与动作。
- schema 返回值包含 `locale` 字段（当前语言上下文，`zh`/`en`）。
- 新功能推荐做法：
  1) 新增对应 API 路由；
  2) 在 schema 中新增页面/区块描述；
  3) Web 端无需额外写死菜单即可显示新入口。
- 任务设置自动发现：
  - 对于同时存在 `GET /api/settings/*` 与 `POST /api/settings/*` 的接口，Web schema 会自动补充“独立任务”区块（标记为“自动发现”）。
  - 非任务类设置接口（如 `/api/settings/notification`）会在后端排除，不会被误识别为“独立任务”。
  - 这类区块默认支持“重载/保存”，若路径名以 `auto-` 开头还会自动推导 `runTask`（如 `auto-demo` -> `auto_demo`）。
  - 表单字段会尝试按字段名自动匹配 `/api/options/*` 下拉接口（如 `domainName` -> `/api/options/domain-names`）；若 schema 显式提供 `optionMap`，以显式配置为准。
- 区块工具按钮（`toolActions`）：
  - 任意 section 可配置 `toolActions` 数组生成顶部按钮，常见字段：`label`、`style`、`method`、`endpoint`、`payload`。
  - 交互字段：`confirm`、`promptJson`、`promptMessage`、`promptQuery`、`defaultQuery`。
  - 刷新字段：`refresh` 支持 `status/network/schema/page`（可写单值或数组）。
  - 提示字段：`successMessage`（留空可关闭成功提示）。
  - 模板变量：`endpoint/payload/confirm/successMessage` 支持 `{{...}}`，当前可用如 `{{activePageId}}`、`{{sectionId}}`、`{{sectionTitle}}`、`{{schedulerSelectedGroup}}`。
- 页面深链：
  - Web 端支持 `#page=<pageId>` 直达指定页（例如接口中心可用 `#page=route-center`）。
  - 接口中心也支持独立页面入口：`/web/api-center.html`（无需 hash）。
- 接口中心（`route-center`）：
  - 已迁移为 FastAPI 标准文档入口，内置 Swagger/ReDoc 与 OpenAPI Schema 快捷访问。
  - 同页保留“兼容路由调试”列表，便于继续做快速 GET/POST 联调。
- 通知中心（`notification-center`）：
  - WebUI 新增通知配置页，覆盖 Webhook/WS/UWP/飞书/OneBot/企业微信/邮箱/Bark/Telegram/xx推送/钉钉/Discord/ServerChan。
  - 页面内支持按通道触发测试通知（调用 `/api/notification/test`）。
  - 额外提供“浏览器推送（WebUI 打开时）”能力：可在页面内申请浏览器通知权限、发送测试通知，并对运行状态/警告变化做本地推送。
- 中英文切换：
  - 侧栏提供 `中文 / English` 语言切换，语言选择会写入本地存储并在刷新后保留。
  - 枚举/选项值会按当前语言显示（如 `Enabled/Disabled`、星期与计划项）。
  - 部分输入框（如调度器计划项）支持中英文值自动归一化：输入展示语种后会自动映射回后端期望值。
- 前端请求 schema 时会附带 `lang` 查询参数（`/api/ui/schema?lang=...`），便于后端后续扩展多语言 schema。
- `/api/ui/schema` 支持 `lang` 查询参数（如 `zh` / `en`），后端会规范化并在响应 `locale` 字段中回显。
- `/api/ui/schema` 支持 `force=1` 强制重建 schema；默认会做短时缓存以降低首次加载后的重复构建开销。
- 后端内置的基础功能字段（如自动拾取、自动剧情）会随 `lang` 返回对应语言标签，减少前端硬编码翻译成本。
- `/api/ui/routes` 提供当前可访问的 GET/POST 路由清单，可用于 Web 端接口浏览与调试页。
- `/api/ui/i18n` 提供 Web 端语言词典：
  - 示例：`/api/ui/i18n?lang=en`
  - 词典来源为软件现有 i18n 文件（`User/I18n/<culture>.json`），Web 端优先使用该词典做翻译。
  - 若词典缺失某些词条，前端会使用内置 fallback 文案保证可用性。

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
- `auto_leyline`
- `auto_artifact_salvage`
- `get_grid_icons`
- `grid_icons_accuracy`
- `auto_redeem_code`

`params` 取值与各任务参数一致，可参考对应 `/api/settings/*` 返回内容。

### 6.4 高级配置接口
- 需在本地设置中开启“**允许 Web 高级配置接口**”。
- 读取：`GET /api/config/get?path=...`
- 写入：`POST /api/config/set`，请求体示例：
  ```json
  { "path": "OtherConfig.UiCultureInfoName", "value": "zh-CN" }
  ```
- 安全策略：
  - 敏感字段（如 AI Key、WebDav 密码、集群 Token 等）会被拒绝读写。
  - 非敏感字段可用于 Web 端“配置中心”进行动态编辑。

### 6.5 一条龙增强接口
- `GET /api/one-dragon/options`：返回 Web 可视化编辑用的任务目录、字段选项映射、字段顺序建议。
- `POST /api/one-dragon/config/clone`：克隆指定配置（可传 `newName`，不传则自动生成）。
- `POST /api/one-dragon/config/rename`：重命名配置。
- `POST /api/one-dragon/config/delete`：删除配置（至少保留 1 个配置）。

### 6.6 调度器增强接口
- `GET /api/options/script-project-statuses`：调度器项目状态可选值。
- `GET /api/options/script-project-schedules`：调度器项目计划可选值。
- `POST /api/scripts/group/copy`：复制配置组（可传 `newName`，不传则自动生成）。
- `POST /api/scripts/group/reverse`：倒置当前配置组任务顺序。
- `POST /api/scripts/group/set-next`：设置或清除“下次从此项开始”。
- `POST /api/scripts/group/set-next-group`：设置或清除“连续执行时从该配置组开始”。
- `POST /api/scripts/group/run-from`：设置起点并立即从该项开始运行当前组。
- `POST /api/scripts/group/export-merged`：导出当前组 Pathing 项目的控制文件合并结果到日志目录。
- `POST /api/scripts/group/add-items`：支持 `shellCommands` 数组，可直接低代码追加 Shell 任务。
- `POST /api/scripts/group/batch-update`：按索引批量更新 `status` / `schedule` / `runNum`（`indices` 为空时默认更新整组）。

### 6.7 集群跨域调用（CORS）
- 集群入口 `/api/cluster/*` 已支持浏览器跨域预检：
  - 允许方法：`GET, POST, OPTIONS`
  - 允许头：`Content-Type, Authorization, X-BGI-Cluster-Token, X-BGI-Token`
- 可直接从第三方前端管理台发起 `fetch` 请求（前提：带上正确 Token）。

### 6.8 对接示例
- 详细示例见：[cluster_api_examples.md](./cluster_api_examples.md)
- 包含：
  - `curl` 快速自检
  - Python（`requests`）
  - Node.js（原生 `fetch`）
  - Go（`net/http`）

## 7. 示例
### 7.1 Web 登录（推荐）
```bash
# 登录获取会话（浏览器一般由 /login 页面自动处理）
curl -X POST http://127.0.0.1:50000/api/auth/login \
  -H "Content-Type: application/json" \
  -d "{\"username\":\"admin\",\"password\":\"password\",\"remember\":true}"
```

### 7.2 普通 API（Basic Auth，脚本兼容）
```bash
# 获取状态
curl -u admin:password http://127.0.0.1:50000/api/status
```

### 7.3 集群 API
```bash
# 获取状态（集群）
curl -H "X-BGI-Cluster-Token: <token>" http://192.168.1.10:50000/api/cluster/status
```

### 7.4 启动一条龙
```bash
curl -X POST \
  -H "Content-Type: application/json" \
  -H "X-BGI-Cluster-Token: <token>" \
  -d '{"name":"默认配置"}' \
  http://192.168.1.10:50000/api/cluster/one-dragon/run
```

---
如需扩展/新增接口，请以 `BetterGenshinImpact/Service/Remote/WebRemoteService.cs` 为准。
