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

    [Fact]
    public void CoerceGeneralKnowledgeToolCalls_ShouldReplaceScriptSearch_WithWebSearch_ForCharacterTrainingQuestion()
    {
        var method = GetCoerceGeneralKnowledgeToolCallsMethod();
        var intent = CreateIntentClassification();
        var toolCalls = CreateToolCallList(("bgi.script.search", "{\"query\":\"练可莉ability.json\",\"limit\":10}"));
        const string userText = "我想要练可莉需要什么";
        object?[] args = [userText, toolCalls, intent, null];

        var rewritten = method.Invoke(null, args);

        Assert.NotNull(rewritten);
        var summary = SummarizeToolCallList(rewritten!);
        Assert.Contains("bgi.web.search", summary);
        Assert.DoesNotContain("bgi.script.search", summary);
        Assert.Contains("原神", summary);
        Assert.Contains("可莉", summary);
    }

    [Fact]
    public void DefaultSystemPrompt_ShouldPrioritizeWebSearch_ForCharacterTrainingQuestions()
    {
        var prompt = GetPrivateConstString("DefaultSystemPrompt");

        Assert.Contains("角色培养、突破、天赋、武器、圣遗物、配队、材料需求时，优先调用 bgi.web.search", prompt);
        Assert.Contains("这类请求不要调用 bgi.script.search", prompt);
        Assert.Contains("我想要练可莉需要什么", prompt);
    }

    [Fact]
    public void IntentClassifierPrompt_ShouldMarkCharacterMaterials_AsNonPathing()
    {
        var prompt = GetPrivateConstString("IntentClassifierPrompt");

        Assert.Contains("角色知识 / 培养 / 突破 / 天赋 / 武器 / 圣遗物 / 配队 / 机制问答 -> pathingIntent=false", prompt);
        Assert.Contains("材料", prompt);
        Assert.Contains("角色培养", prompt);
    }

    [Fact]
    public void NormalizeAutomatableMaterialQuery_ShouldKeepLocalSpecialty_AndCollapseEnemyDropFamily()
    {
        var method = GetStaticMethod("NormalizeAutomatableMaterialQuery", typeof(string));

        var localSpecialty = method.Invoke(null, ["慕风蘑菇"]) as string;
        var enemyDrop = method.Invoke(null, ["导能绘卷"]) as string;
        var book = method.Invoke(null, ["「自由」的哲学"]) as string;

        Assert.Equal("慕风蘑菇", localSpecialty);
        Assert.Equal("绘卷", enemyDrop);
        Assert.Null(book);
    }

    [Fact]
    public void BuildCharacterAutomationSearchCalls_ShouldCreatePathingSearches_FromCharacterMaterials()
    {
        var method = GetBuildCharacterAutomationSearchCallsMethod();
        var intent = CreateIntentClassification();
        var executedCalls = CreateExecutedCallList((
            "bgi.web.search",
            "{\"query\":\"原神 可莉 培养材料\",\"maxResults\":3}",
            """
            {"provider":"honeyhunter_character_data","results":[{"materials":{"characterAscension":[{"name":"慕风蘑菇","quantity":168},{"name":"导能绘卷","quantity":18},{"name":"常燃火种","quantity":46}]}}]}
            """,
            false));

        var result = method.Invoke(null, ["帮我准备可莉突破要用的东西，并直接运行能做的部分", intent, executedCalls]);

        Assert.NotNull(result);
        var summary = SummarizeToolCallList(result!);
        Assert.Contains("bgi.script.search", summary);
        Assert.Contains("慕风蘑菇", summary);
        Assert.Contains("绘卷", summary);
        Assert.DoesNotContain("常燃火种", summary);
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

    private static string GetPrivateConstString(string name)
    {
        var field = GetAiChatViewModelType().GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = field!.GetRawConstantValue() as string;
        Assert.NotNull(value);
        return value!;
    }

    private static MethodInfo GetCoerceGeneralKnowledgeToolCallsMethod()
    {
        var type = GetAiChatViewModelType();
        var intentType = GetNestedType("IntentClassification");
        var toolCallType = GetNestedType("McpToolCall");
        var listType = typeof(IReadOnlyList<>).MakeGenericType(toolCallType);
        var method = type.GetMethod(
            "CoerceGeneralKnowledgeToolCalls",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string), listType, intentType, typeof(string).MakeByRefType()],
            modifiers: null);
        Assert.NotNull(method);
        return method!;
    }

    private static MethodInfo GetBuildCharacterAutomationSearchCallsMethod()
    {
        var type = GetAiChatViewModelType();
        var intentType = GetNestedType("IntentClassification");
        var executedType = GetNestedType("ExecutedMcpToolCall");
        var listType = typeof(IReadOnlyList<>).MakeGenericType(executedType);
        var method = type.GetMethod(
            "BuildCharacterAutomationSearchCalls",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string), intentType, listType],
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

    private static object CreateExecutedCallList(params (string Name, string ArgumentsJson, string Content, bool IsError)[] items)
    {
        var executedType = GetNestedType("ExecutedMcpToolCall");
        var resultType = typeof(BetterGenshinImpact.Model.Ai.McpToolCallResult);
        var listType = typeof(List<>).MakeGenericType(executedType);
        var addMethod = listType.GetMethod("Add");
        Assert.NotNull(addMethod);

        var list = Activator.CreateInstance(listType);
        Assert.NotNull(list);

        foreach (var item in items)
        {
            var toolResult = Activator.CreateInstance(resultType, item.IsError, item.Content, null);
            Assert.NotNull(toolResult);
            var executed = Activator.CreateInstance(
                executedType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [item.Name, item.ArgumentsJson, toolResult],
                culture: null);
            Assert.NotNull(executed);
            addMethod!.Invoke(list, [executed]);
        }

        return list!;
    }

    private static Type GetNestedType(string name)
    {
        var type = GetAiChatViewModelType().GetNestedType(name, BindingFlags.NonPublic);
        Assert.NotNull(type);
        return type!;
    }

    private static string SummarizeToolCallList(object toolCallList)
    {
        var items = toolCallList as System.Collections.IEnumerable;
        Assert.NotNull(items);

        var parts = new List<string>();
        foreach (var item in items!)
        {
            Assert.NotNull(item);
            var type = item!.GetType();
            var name = type.GetProperty("Name")?.GetValue(item) as string;
            var argumentsJson = type.GetProperty("ArgumentsJson")?.GetValue(item) as string;
            parts.Add($"{name}|{argumentsJson}");
        }

        return string.Join("\n", parts);
    }
}
