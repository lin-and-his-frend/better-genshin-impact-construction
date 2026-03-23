using System.Reflection;

namespace BetterGenshinImpact.UnitTest.HelperTests;

public class AiChatExecutionPlanTests
{
    [Fact]
    public void TryExtractAssistantReplyFromEnvelope_ShouldReturnReply_WhenEnvelopeContainsReplyAndToolCalls()
    {
        var method = GetStaticMethod("TryExtractAssistantReplyFromEnvelope", typeof(string));
        const string rawReply = """
                                {"reply":"任务列表：\n1. 读取当前状态\n2. 汇总结论","toolCalls":[{"name":"bgi.get_features","arguments":{}}]}
                                """;

        var reply = method.Invoke(null, [rawReply]) as string;

        Assert.Equal("任务列表：\n1. 读取当前状态\n2. 汇总结论", reply);
    }

    [Fact]
    public void ShouldKeepAssistantReplyAsExecutionPlan_ShouldReturnTrue_ForTaskListReply()
    {
        var method = GetStaticMethod("ShouldKeepAssistantReplyAsExecutionPlan", typeof(string));
        const string reply = "任务列表：\n1. 读取当前状态\n2. 修改配置\n3. 汇总结论";

        var keep = (bool)method.Invoke(null, [reply])!;

        Assert.True(keep);
    }

    [Fact]
    public void ShouldKeepAssistantReplyAsExecutionPlan_ShouldReturnFalse_ForSimpleKnowledgeReply()
    {
        var method = GetStaticMethod("ShouldKeepAssistantReplyAsExecutionPlan", typeof(string));
        const string reply = "地脉花是原神中的一种挑战内容，领取后会消耗树脂。";

        var keep = (bool)method.Invoke(null, [reply])!;

        Assert.False(keep);
    }

    [Fact]
    public void BuildFallbackExecutionPlanReply_ShouldIncludeToolStepsAndSummary()
    {
        var method = GetStaticMethod("BuildFallbackExecutionPlanReply", typeof(IReadOnlyList<string>));
        IReadOnlyList<string> toolNames = ["bgi.get_features", "bgi.set_features"];

        var reply = method.Invoke(null, [toolNames]) as string;

        Assert.NotNull(reply);
        Assert.Contains("任务列表", reply);
        Assert.Contains("bgi.get_features", reply);
        Assert.Contains("bgi.set_features", reply);
        Assert.Contains("整理结论并回复给你", reply);
    }

    [Fact]
    public void ShouldShowExecutionPlanForToolCalls_ShouldReturnTrue_WhenEnvelopeReplyAlreadyContainsPlan()
    {
        var method = GetShouldShowExecutionPlanMethod();
        var intent = CreateIntentClassification();
        var toolCalls = CreateToolCallList(("search_docs", "{\"query\":\"自动战斗\"}"));
        const string rawReply = """
                                {"reply":"任务列表：\n1. 检索相关文档\n2. 整理操作建议","toolCalls":[{"name":"search_docs","arguments":{"query":"自动战斗"}}]}
                                """;

        var result = (bool)method.Invoke(null, [intent, toolCalls, rawReply])!;

        Assert.True(result);
    }

    [Fact]
    public void ShouldShowExecutionPlanForToolCalls_ShouldReturnFalse_ForMultipleInformationalToolCallsWithoutPlan()
    {
        var method = GetShouldShowExecutionPlanMethod();
        var intent = CreateIntentClassification();
        var toolCalls = CreateToolCallList(
            ("search_docs", "{\"query\":\"自动战斗\"}"),
            ("get_feature_detail", "{\"feature\":\"autoFight\"}"));

        var result = (bool)method.Invoke(null, [intent, toolCalls, null])!;

        Assert.False(result);
    }

    [Fact]
    public void ShouldShowExecutionPlanForToolCalls_ShouldReturnFalse_ForSingleInformationalToolCallWithoutPlan()
    {
        var method = GetShouldShowExecutionPlanMethod();
        var intent = CreateIntentClassification();
        var toolCalls = CreateToolCallList(("search_docs", "{\"query\":\"自动战斗\"}"));

        var result = (bool)method.Invoke(null, [intent, toolCalls, null])!;

        Assert.False(result);
    }

    [Fact]
    public void BuildVisibleExecutionPlanReply_ShouldFallbackToFilteredToolCalls_WhenEnvelopeToolCallsChanged()
    {
        var method = GetBuildVisibleExecutionPlanReplyMethod();
        var toolCalls = CreateToolCallList(("search_docs", "{\"query\":\"自动战斗\"}"));
        const string rawReply = """
                                {"reply":"任务列表：\n1. 检索官网文档\n2. 查询功能说明\n3. 整理结果","toolCalls":[{"name":"search_docs","arguments":{"query":"自动战斗"}},{"name":"get_feature_detail","arguments":{"feature":"autoFight"}}]}
                                """;

        var reply = method.Invoke(null, [rawReply, toolCalls]) as string;

        Assert.NotNull(reply);
        Assert.Contains("search_docs", reply);
        Assert.DoesNotContain("get_feature_detail", reply);
        Assert.Contains("任务列表", reply);
    }

    [Fact]
    public void BuildVisibleExecutionPlanReply_ShouldSanitizeNonOfficialUrl_WhenKeepingAssistantPlan()
    {
        var method = GetBuildVisibleExecutionPlanReplyMethod();
        var toolCalls = CreateToolCallList(("search_docs", "{\"query\":\"自动战斗\"}"));
        const string rawReply = """
                                {"reply":"任务列表：\n1. 查看 https://evil.example/help 获取说明\n2. 继续执行","toolCalls":[{"name":"search_docs","arguments":{"query":"自动战斗"}}]}
                                """;

        var reply = method.Invoke(null, [rawReply, toolCalls]) as string;

        Assert.NotNull(reply);
        Assert.DoesNotContain("https://evil.example/help", reply);
        Assert.Contains("hxxps://evil.example/help", reply);
    }

    [Fact]
    public void BuildAutoExecuteBlockedReply_ShouldMentionBlockedToolsAndSetting()
    {
        var method = GetStaticMethod("BuildAutoExecuteBlockedReply", typeof(IReadOnlyList<string>));
        IReadOnlyList<string> toolNames = ["search_docs", "get_feature_detail"];

        var reply = method.Invoke(null, [toolNames]) as string;

        Assert.NotNull(reply);
        Assert.Contains("search_docs", reply);
        Assert.Contains("get_feature_detail", reply);
        Assert.Contains("自动执行 MCP 工具调用", reply);
    }

    [Fact]
    public void SanitizeExecutionPlanReplyForDisplay_ShouldStripNonOfficialMarkdownImage()
    {
        var method = GetStaticMethod("SanitizeExecutionPlanReplyForDisplay", typeof(string));
        const string reply = "任务列表：\n1. ![追踪图](https://evil.example/plan.png)\n2. 继续执行";

        var sanitized = method.Invoke(null, [reply]) as string;

        Assert.NotNull(sanitized);
        Assert.DoesNotContain("evil.example", sanitized);
        Assert.Contains("[图片已省略: 追踪图]", sanitized);
    }

    [Fact]
    public void SanitizeExecutionPlanReplyForDisplay_ShouldKeepBettergiMarkdownLink()
    {
        var method = GetStaticMethod("SanitizeExecutionPlanReplyForDisplay", typeof(string));
        const string reply = "任务列表：\n1. 查看[官网文档](https://bettergi.com/help)";

        var sanitized = method.Invoke(null, [reply]) as string;

        Assert.Equal(reply, sanitized);
    }

    [Fact]
    public void SanitizeExecutionPlanReplyForDisplay_ShouldKeepBettergiMarkdownImage()
    {
        var method = GetStaticMethod("SanitizeExecutionPlanReplyForDisplay", typeof(string));
        const string reply = "任务列表：\n1. ![官网示意图](https://bettergi.com/assets/plan.png)\n2. 继续执行";

        var sanitized = method.Invoke(null, [reply]) as string;

        Assert.Equal(reply, sanitized);
    }

    [Fact]
    public void SanitizeExecutionPlanReplyForDisplay_ShouldKeepBettergiSubdomainRawUrl()
    {
        var method = GetStaticMethod("SanitizeExecutionPlanReplyForDisplay", typeof(string));
        const string reply = "任务列表：\n1. 访问 https://docs.bettergi.com/guide 获取说明";

        var sanitized = method.Invoke(null, [reply]) as string;

        Assert.Equal(reply, sanitized);
    }

    [Fact]
    public void SanitizeExecutionPlanReplyForDisplay_ShouldNeutralizeNonOfficialRawUrl()
    {
        var method = GetStaticMethod("SanitizeExecutionPlanReplyForDisplay", typeof(string));
        const string reply = "任务列表：\n1. 打开 https://evil.example/help 获取说明";

        var sanitized = method.Invoke(null, [reply]) as string;

        Assert.NotNull(sanitized);
        Assert.DoesNotContain("https://evil.example/help", sanitized);
        Assert.Contains("hxxps://evil.example/help", sanitized);
    }

    [Fact]
    public void SanitizeExecutionPlanReplyForDisplay_ShouldBlockBettergiLookalikeHost()
    {
        var method = GetStaticMethod("SanitizeExecutionPlanReplyForDisplay", typeof(string));
        const string reply = "任务列表：\n1. 打开 https://bettergi.com.evil.example/phish";

        var sanitized = method.Invoke(null, [reply]) as string;

        Assert.NotNull(sanitized);
        Assert.DoesNotContain("https://bettergi.com.evil.example/phish", sanitized);
        Assert.Contains("hxxps://bettergi.com.evil.example/phish", sanitized);
    }

    private static MethodInfo GetStaticMethod(string name, params Type[] parameterTypes)
    {
        var type = GetAiChatViewModelType();
        var method = type.GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        return method!;
    }

    private static Type GetAiChatViewModelType()
    {
        var coreAssembly = typeof(BetterGenshinImpact.Core.Config.Global).Assembly;
        var type = coreAssembly.GetType("BetterGenshinImpact.ViewModel.Pages.AiChatViewModel");
        Assert.NotNull(type);
        return type!;
    }

    private static MethodInfo GetShouldShowExecutionPlanMethod()
    {
        var type = GetAiChatViewModelType();
        var intentType = GetNestedType("IntentClassification");
        var toolCallType = GetNestedType("McpToolCall");
        var listType = typeof(IReadOnlyList<>).MakeGenericType(toolCallType);
        var method = type.GetMethod(
            "ShouldShowExecutionPlanForToolCalls",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [intentType, listType, typeof(string)],
            modifiers: null);
        Assert.NotNull(method);
        return method!;
    }

    private static MethodInfo GetBuildVisibleExecutionPlanReplyMethod()
    {
        var type = GetAiChatViewModelType();
        var toolCallType = GetNestedType("McpToolCall");
        var listType = typeof(IReadOnlyList<>).MakeGenericType(toolCallType);
        var method = type.GetMethod(
            "BuildVisibleExecutionPlanReply",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string), listType],
            modifiers: null);
        Assert.NotNull(method);
        return method!;
    }

    private static object CreateIntentClassification(
        bool pathingPriorityIntent = false,
        bool scriptSubscribeIntent = false,
        bool scriptDetailIntent = false,
        bool docHelpIntent = false,
        bool downloadIntent = false,
        bool statusQueryIntent = false,
        bool realtimeFeatureQueryIntent = false,
        bool? desiredFeatureValue = null,
        bool allFeaturesRequest = false,
        bool isAllRequest = false,
        string? featureKey = null,
        string classifierSource = "test",
        string? classifierReason = null)
    {
        var intentType = GetNestedType("IntentClassification");
        var instance = Activator.CreateInstance(
            intentType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                pathingPriorityIntent,
                scriptSubscribeIntent,
                scriptDetailIntent,
                docHelpIntent,
                downloadIntent,
                statusQueryIntent,
                realtimeFeatureQueryIntent,
                desiredFeatureValue,
                allFeaturesRequest,
                isAllRequest,
                featureKey,
                classifierSource,
                classifierReason
            ],
            culture: null);
        Assert.NotNull(instance);
        return instance!;
    }

    private static object CreateToolCallList(params (string Name, string ArgumentsJson)[] items)
    {
        var toolCallType = GetNestedType("McpToolCall");
        var listType = typeof(List<>).MakeGenericType(toolCallType);
        var addMethod = listType.GetMethod("Add");
        Assert.NotNull(addMethod);

        var list = Activator.CreateInstance(listType);
        Assert.NotNull(list);

        foreach (var item in items)
        {
            var toolCall = Activator.CreateInstance(
                toolCallType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [item.Name, item.ArgumentsJson],
                culture: null);
            Assert.NotNull(toolCall);
            addMethod!.Invoke(list, [toolCall]);
        }

        return list!;
    }

    private static Type GetNestedType(string name)
    {
        var type = GetAiChatViewModelType().GetNestedType(name, BindingFlags.NonPublic);
        Assert.NotNull(type);
        return type!;
    }
}
