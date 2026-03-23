const host = (process.env.BGI_HOST || "http://127.0.0.1:50000").replace(/\/+$/, "");
const token = process.env.BGI_CLUSTER_TOKEN || "";

if (!token) {
  console.error("Missing env: BGI_CLUSTER_TOKEN");
  process.exit(2);
}

async function request(path, init = {}) {
  const res = await fetch(`${host}${path}`, {
    ...init,
    headers: {
      "X-BGI-Cluster-Token": token,
      ...(init.headers || {})
    }
  });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`${res.status} ${text}`);
  }
  return text ? JSON.parse(text) : {};
}

async function main() {
  const action = process.argv[2] || "health";
  if (action === "health") {
    console.log(JSON.stringify(await request("/api/cluster/health"), null, 2));
    return;
  }
  if (action === "meta") {
    console.log(JSON.stringify(await request("/api/cluster/meta"), null, 2));
    return;
  }
  if (action === "routes") {
    console.log(JSON.stringify(await request("/api/cluster/routes"), null, 2));
    return;
  }
  if (action === "openapi") {
    console.log(JSON.stringify(await request("/api/cluster/openapi.json"), null, 2));
    return;
  }
  if (action === "run-one-dragon") {
    const name = process.argv[3] || "默认配置";
    const result = await request("/api/cluster/one-dragon/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name })
    });
    console.log(JSON.stringify(result, null, 2));
    return;
  }
  if (action === "run-task") {
    const task = process.argv[3] || "auto_leyline";
    const result = await request("/api/cluster/tasks/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ task, params: {} })
    });
    console.log(JSON.stringify(result, null, 2));
    return;
  }
  if (action === "notification-settings") {
    console.log(JSON.stringify(await request("/api/cluster/settings/notification"), null, 2));
    return;
  }
  if (action === "notification-test") {
    const channel = process.argv[3] || "webhook";
    const result = await request("/api/cluster/notification/test", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ channel })
    });
    console.log(JSON.stringify(result, null, 2));
    return;
  }

  console.error(`Unknown action: ${action}`);
  process.exit(2);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
