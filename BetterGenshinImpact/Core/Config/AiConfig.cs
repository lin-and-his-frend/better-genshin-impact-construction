using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Serialization;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// AI 问答配置（OpenAI 兼容）
/// </summary>
[Serializable]
public partial class AiConfig : ObservableObject
{
    /// <summary>
    /// OpenAI 兼容接口基础地址
    /// </summary>
    [ObservableProperty]
    private string _baseUrl = "https://api.siliconflow.cn/v1";

    /// <summary>
    /// API Key
    /// </summary>
    [ObservableProperty]
    private string _apiKey = string.Empty;

    /// <summary>
    /// 模型名称
    /// </summary>
    [ObservableProperty]
    private string _model = "Qwen/Qwen2.5-72B-Instruct";

    /// <summary>
    /// 启用 JSON Mode（兼容层或其他结构化输出场景的附加参数）
    /// </summary>
    [ObservableProperty]
    private bool _useJsonMode = true;

    /// <summary>
    /// 启用工具调用硬兼容层（旧 JSON / 文本解析链）
    /// </summary>
    [ObservableProperty]
    private bool _useLegacyToolCallCompatibility = false;

    /// <summary>
    /// 启用流式输出（stream=true），聊天窗口将使用打字机效果渐进显示回复
    /// </summary>
    [ObservableProperty]
    private bool _useStreamingResponse = false;

    /// <summary>
    /// 允许 AI 自动执行 MCP 工具调用（高风险，默认关闭）
    /// </summary>
    [ObservableProperty]
    private bool _autoExecuteMcpToolCalls = false;

    /// <summary>
    /// 在 AI 聊天窗口显示 MCP 可视化过程消息（默认关闭）
    /// </summary>
    [ObservableProperty]
    private bool _showMcpVisualizationOutput = false;

    /// <summary>
    /// 最大上下文长度（字符数，包含系统提示和历史消息）
    /// </summary>
    [ObservableProperty]
    private int _maxContextChars = 80000;

    /// <summary>
    /// 启用向量检索（用于脚本语义搜索，降低脚本列表导致的 token 溢出）
    /// </summary>
    [ObservableProperty]
    private bool _vectorSearchEnabled = true;

    /// <summary>
    /// 向量模型接口基础地址（OpenAI Embeddings 兼容）
    /// </summary>
    [ObservableProperty]
    private string _vectorBaseUrl = "https://api.siliconflow.cn/v1";

    /// <summary>
    /// 向量模型是否复用聊天模型的 API 站点与 Key
    /// </summary>
    [ObservableProperty]
    private bool _vectorUseSameApiSite = true;

    /// <summary>
    /// 向量模型 API Key（留空时回退使用上面的 API Key）
    /// </summary>
    [ObservableProperty]
    private string _vectorApiKey = string.Empty;

    /// <summary>
    /// 向量模型名称
    /// </summary>
    [ObservableProperty]
    private string _vectorModel = "BAAI/bge-m3";

    /// <summary>
    /// 启用重排模型（用于提升语义检索结果相关性）
    /// </summary>
    [ObservableProperty]
    private bool _vectorRerankEnabled = false;

    /// <summary>
    /// 重排模型名称（OpenAI 兼容 rerank 接口）
    /// </summary>
    [ObservableProperty]
    private string _vectorRerankModel = "BAAI/bge-reranker-v2-m3";

    [JsonIgnore]
    public bool VectorEndpointEditable => VectorSearchEnabled && !VectorUseSameApiSite;

    partial void OnVectorSearchEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(VectorEndpointEditable));
    }

    partial void OnVectorUseSameApiSiteChanged(bool value)
    {
        OnPropertyChanged(nameof(VectorEndpointEditable));
    }
}
