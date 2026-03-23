import json
import os
import sys
from typing import Any

import requests


HOST = os.getenv("BGI_HOST", "http://127.0.0.1:50000").rstrip("/")
TOKEN = os.getenv("BGI_CLUSTER_TOKEN", "")
TIMEOUT = float(os.getenv("BGI_TIMEOUT", "15"))


def _headers() -> dict[str, str]:
    if not TOKEN:
        raise RuntimeError("Missing BGI_CLUSTER_TOKEN")
    return {
        "X-BGI-Cluster-Token": TOKEN,
        "Content-Type": "application/json",
    }


def get_json(path: str) -> Any:
    resp = requests.get(f"{HOST}{path}", headers=_headers(), timeout=TIMEOUT)
    resp.raise_for_status()
    return resp.json()


def post_json(path: str, payload: Any) -> Any:
    resp = requests.post(
        f"{HOST}{path}",
        headers=_headers(),
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        timeout=TIMEOUT,
    )
    resp.raise_for_status()
    return resp.json()


def main() -> int:
    action = sys.argv[1] if len(sys.argv) > 1 else "health"
    if action == "health":
        print(json.dumps(get_json("/api/cluster/health"), ensure_ascii=False, indent=2))
        return 0
    if action == "meta":
        print(json.dumps(get_json("/api/cluster/meta"), ensure_ascii=False, indent=2))
        return 0
    if action == "routes":
        print(json.dumps(get_json("/api/cluster/routes"), ensure_ascii=False, indent=2))
        return 0
    if action == "openapi":
        print(json.dumps(get_json("/api/cluster/openapi.json"), ensure_ascii=False, indent=2))
        return 0
    if action == "run-one-dragon":
        name = sys.argv[2] if len(sys.argv) > 2 else "默认配置"
        print(json.dumps(post_json("/api/cluster/one-dragon/run", {"name": name}), ensure_ascii=False, indent=2))
        return 0
    if action == "run-task":
        task = sys.argv[2] if len(sys.argv) > 2 else "auto_leyline"
        print(json.dumps(post_json("/api/cluster/tasks/run", {"task": task, "params": {}}), ensure_ascii=False, indent=2))
        return 0
    if action == "notification-settings":
        print(json.dumps(get_json("/api/cluster/settings/notification"), ensure_ascii=False, indent=2))
        return 0
    if action == "notification-test":
        channel = sys.argv[2] if len(sys.argv) > 2 else "webhook"
        print(json.dumps(post_json("/api/cluster/notification/test", {"channel": channel}), ensure_ascii=False, indent=2))
        return 0

    print("Unknown action:", action)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
