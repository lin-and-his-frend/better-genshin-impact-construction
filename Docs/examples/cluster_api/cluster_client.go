package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"
)

func env(name, fallback string) string {
	v := strings.TrimSpace(os.Getenv(name))
	if v == "" {
		return fallback
	}
	return v
}

func request(client *http.Client, method, host, token, path string, payload any) ([]byte, error) {
	var body io.Reader
	if payload != nil {
		b, err := json.Marshal(payload)
		if err != nil {
			return nil, err
		}
		body = bytes.NewReader(b)
	}

	req, err := http.NewRequest(method, host+path, body)
	if err != nil {
		return nil, err
	}
	req.Header.Set("X-BGI-Cluster-Token", token)
	if payload != nil {
		req.Header.Set("Content-Type", "application/json")
	}

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	data, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("status=%d body=%s", resp.StatusCode, string(data))
	}
	return data, nil
}

func main() {
	host := strings.TrimRight(env("BGI_HOST", "http://127.0.0.1:50000"), "/")
	token := env("BGI_CLUSTER_TOKEN", "")
	action := env("BGI_ACTION", "health")
	if len(os.Args) > 1 {
		action = os.Args[1]
	}
	if token == "" {
		fmt.Println("Missing env: BGI_CLUSTER_TOKEN")
		os.Exit(2)
	}

	client := &http.Client{Timeout: 15 * time.Second}
	var (
		data []byte
		err  error
	)

	switch action {
	case "health":
		data, err = request(client, http.MethodGet, host, token, "/api/cluster/health", nil)
	case "meta":
		data, err = request(client, http.MethodGet, host, token, "/api/cluster/meta", nil)
	case "routes":
		data, err = request(client, http.MethodGet, host, token, "/api/cluster/routes", nil)
	case "openapi":
		data, err = request(client, http.MethodGet, host, token, "/api/cluster/openapi.json", nil)
	case "run-one-dragon":
		name := "默认配置"
		if len(os.Args) > 2 {
			name = os.Args[2]
		}
		data, err = request(client, http.MethodPost, host, token, "/api/cluster/one-dragon/run", map[string]any{"name": name})
	case "run-task":
		task := "auto_leyline"
		if len(os.Args) > 2 {
			task = os.Args[2]
		}
		data, err = request(client, http.MethodPost, host, token, "/api/cluster/tasks/run", map[string]any{"task": task, "params": map[string]any{}})
	case "notification-settings":
		data, err = request(client, http.MethodGet, host, token, "/api/cluster/settings/notification", nil)
	case "notification-test":
		channel := "webhook"
		if len(os.Args) > 2 {
			channel = os.Args[2]
		}
		data, err = request(client, http.MethodPost, host, token, "/api/cluster/notification/test", map[string]any{"channel": channel})
	default:
		fmt.Println("Unknown action:", action)
		os.Exit(2)
	}

	if err != nil {
		fmt.Println("Request failed:", err)
		os.Exit(1)
	}

	fmt.Println(string(data))
}
