# BetterGI 集群 API 对接示例

本文档给出可直接复制的集群 API 调用示例，目标是让第三方集群/控制台快速接入。

仓库内可直接运行的示例文件：
- `Docs/examples/cluster_api/cluster_quick_check.ps1`
- `Docs/examples/cluster_api/cluster_client.py`
- `Docs/examples/cluster_api/cluster_client.mjs`
- `Docs/examples/cluster_api/cluster_client.go`

## 1. 前置条件
- 在 BetterGI 中开启：`设置 -> Web 远程控制 -> 启用集群群控 API`
- 获取 `Cluster Token`
- （可选）配置白名单 IP

示例统一使用：
- `HOST=http://192.168.1.10:50000`
- `TOKEN=<your-cluster-token>`

## 2. 快速自检（curl）
### 2.1 健康检查
```bash
curl -H "X-BGI-Cluster-Token: $TOKEN" \
  "$HOST/api/cluster/health"
```

### 2.2 查看入口元信息
```bash
curl -H "X-BGI-Cluster-Token: $TOKEN" \
  "$HOST/api/cluster/meta"
```

### 2.3 查看可调用路由清单
```bash
curl -H "X-BGI-Cluster-Token: $TOKEN" \
  "$HOST/api/cluster/routes"
```
> 返回值中的 `get/post` 路由已带 `/api/cluster` 前缀，可直接用于集群调用。

### 2.4 获取 OpenAPI（用于自动生成客户端）
```bash
curl -H "X-BGI-Cluster-Token: $TOKEN" \
  "$HOST/api/cluster/openapi.json"
```

### 2.5 启动一条龙
```bash
curl -X POST \
  -H "Content-Type: application/json" \
  -H "X-BGI-Cluster-Token: $TOKEN" \
  -d '{"name":"默认配置"}' \
  "$HOST/api/cluster/one-dragon/run"
```

### 2.6 测试通知通道（Webhook 示例）
```bash
curl -X POST \
  -H "Content-Type: application/json" \
  -H "X-BGI-Cluster-Token: $TOKEN" \
  -d '{"channel":"webhook"}' \
  "$HOST/api/cluster/notification/test"
```

## 3. Python 示例（requests）
```python
import requests

HOST = "http://192.168.1.10:50000"
TOKEN = "<your-cluster-token>"
HEADERS = {"X-BGI-Cluster-Token": TOKEN}

def get_json(path: str):
    resp = requests.get(f"{HOST}{path}", headers=HEADERS, timeout=10)
    resp.raise_for_status()
    return resp.json()

def post_json(path: str, payload: dict):
    resp = requests.post(
        f"{HOST}{path}",
        headers={**HEADERS, "Content-Type": "application/json"},
        json=payload,
        timeout=20,
    )
    resp.raise_for_status()
    return resp.json()

print("health:", get_json("/api/cluster/health"))
print("meta:", get_json("/api/cluster/meta"))
routes = get_json("/api/cluster/routes")
print("route count:", len(routes.get("get", [])), len(routes.get("post", [])))

result = post_json("/api/cluster/one-dragon/run", {"name": "默认配置"})
print("one-dragon run:", result)
```

## 4. Node.js 示例（原生 fetch）
```javascript
const HOST = "http://192.168.1.10:50000";
const TOKEN = "<your-cluster-token>";

async function getJson(path) {
  const res = await fetch(`${HOST}${path}`, {
    headers: { "X-BGI-Cluster-Token": TOKEN }
  });
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
}

async function postJson(path, payload) {
  const res = await fetch(`${HOST}${path}`, {
    method: "POST",
    headers: {
      "X-BGI-Cluster-Token": TOKEN,
      "Content-Type": "application/json"
    },
    body: JSON.stringify(payload ?? {})
  });
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
}

const main = async () => {
  console.log("health", await getJson("/api/cluster/health"));
  console.log("meta", await getJson("/api/cluster/meta"));
  const result = await postJson("/api/cluster/tasks/run", { task: "auto_leyline", params: {} });
  console.log("task run", result);
};

main().catch(err => {
  console.error(err);
  process.exit(1);
});
```

## 5. Go 示例（net/http）
```go
package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"
)

const (
	host  = "http://192.168.1.10:50000"
	token = "<your-cluster-token>"
)

var client = &http.Client{Timeout: 15 * time.Second}

func get(path string) ([]byte, error) {
	req, _ := http.NewRequest(http.MethodGet, host+path, nil)
	req.Header.Set("X-BGI-Cluster-Token", token)
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("status=%d body=%s", resp.StatusCode, string(body))
	}
	return body, nil
}

func post(path string, payload any) ([]byte, error) {
	data, _ := json.Marshal(payload)
	req, _ := http.NewRequest(http.MethodPost, host+path, bytes.NewReader(data))
	req.Header.Set("X-BGI-Cluster-Token", token)
	req.Header.Set("Content-Type", "application/json")
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("status=%d body=%s", resp.StatusCode, string(body))
	}
	return body, nil
}

func main() {
	h, err := get("/api/cluster/health")
	if err != nil {
		panic(err)
	}
	fmt.Println("health:", string(h))

	r, err := post("/api/cluster/one-dragon/run", map[string]any{"name": "默认配置"})
	if err != nil {
		panic(err)
	}
	fmt.Println("run:", string(r))
}
```

## 6. 常见失败排查
- `403 Forbidden`
  - Token 错误，或白名单未放行调用方 IP。
- `404 Not Found`
  - 未开启集群群控 API，或路径不是 `/api/cluster/...`。
- `401 Unauthorized`
  - 你调用的是普通 `/api/...` 路径，走的是 Web 鉴权，不是 Cluster Token 流程。
- 前端跨域报错（CORS）
  - 请确认调用的是 `/api/cluster/*`，且请求头使用允许的 Token 头。
