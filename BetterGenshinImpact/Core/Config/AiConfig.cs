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
    private string _baseUrl = "https://api.openai.com/v1";

    /// <summary>
    /// API Key
    /// </summary>
    [ObservableProperty]
    private string _apiKey = string.Empty;

    /// <summary>
    /// 模型名称
    /// </summary>
    [ObservableProperty]
    private string _model = "gpt-4o-mini";

    /// <summary>
    /// 最大上下文长度（字符数，包含系统提示和历史消息）
    /// </summary>
    [ObservableProperty]
    private int _maxContextChars = 80000;

    /// <summary>
    /// 启用向量检索（用于脚本语义搜索，降低脚本列表导致的 token 溢出）
    /// </summary>
    [ObservableProperty]
    private bool _vectorSearchEnabled = false;

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
