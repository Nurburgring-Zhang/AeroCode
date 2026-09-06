// B3 运行时探测测试：fail-closed（失败=Missing）、探测请求不携带用户数据、配置/模型id/探测响应三依据。
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.Ai;

public class VendorCapabilityProbeTests
{
    private const string UserCanary = "USER-CANARY-x7f-do-not-leak";

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _responder;
        public int RequestCount { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string? LastRequestUrl { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) => _responder = responder;
        public FakeHandler(HttpResponseMessage response) : this((_, _) => response) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            RequestCount++;
            LastRequestUrl = req.RequestUri?.ToString();
            if (req.Content is not null) LastRequestBody = req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(_responder(req, LastRequestBody));
        }
    }

    private static VendorCapabilityProbe MakeProbe(FakeHandler? handler, ProviderConfig? config, string providerId = "p1")
    {
        ProviderConfig? Lookup(string id) => id.Equals(providerId, StringComparison.OrdinalIgnoreCase) ? config : null;
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        return new VendorCapabilityProbe(Lookup, http, NullLogger.Instance);
    }

    private static ProviderConfig OpenAiConfig(string model = "gpt-5.2") => new()
    {
        Id = "p1", DisplayName = UserCanary, Kind = "OpenAICompatible",
        BaseUrl = "https://api.openai.com/v1", DefaultModel = model,
        RequiresApiKey = false
    };

    private static ProviderConfig AnthropicConfig(string model = "claude-5-sonnet") => new()
    {
        Id = "p1", DisplayName = UserCanary, Kind = "AnthropicMessages",
        BaseUrl = "https://api.anthropic.com", DefaultModel = model,
        RequiresApiKey = false
    };

    private static ProviderConfig GeminiConfig(string model = "gemini-2.5-flash") => new()
    {
        Id = "p1", DisplayName = UserCanary, Kind = "OpenAICompatible",
        BaseUrl = "https://generativelanguage.googleapis.com", DefaultModel = model,
        RequiresApiKey = false
    };

    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    // ---------- fail-closed：未知 provider / 不可达端点 / 探测失败 = Missing ----------

    [Fact]
    public async Task UnknownProvider_AllCapabilities_MissingWithZeroHttpRequests()
    {
        var handler = new FakeHandler(Json(200, "{}"));
        var probe = MakeProbe(handler, config: null, providerId: "p1");
        foreach (VendorCapability cap in Enum.GetValues<VendorCapability>())
        {
            var state = await probe.ProbeAsync("p1", cap);
            Assert.Equal(VendorCapabilityState.Missing, state);
        }
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task UnreachableEndpoint_NetworkProbes_Missing()
    {
        var cfg = OpenAiConfig();
        cfg.BaseUrl = "http://127.0.0.1:1/";
        var handler = new FakeHandler(Json(200, "{}")); // 不应被命中
        var probe = MakeProbe(handler, cfg);
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs));
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.PromptCache));
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.Batch));
    }

    [Fact]
    public async Task OpenAi_SchemaRejected400_StructuredOutputs_Missing()
    {
        var handler = new FakeHandler(Json(400, "{\"error\":{\"message\":\"response_format unsupported\"}}"));
        var probe = MakeProbe(handler, OpenAiConfig());
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs));
    }

    [Fact]
    public async Task OpenAi_SchemaAcceptedWithJsonContent_StructuredOutputs_Supported()
    {
        var handler = new FakeHandler(Json(200, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"ok\\\":true}\"}}]}"));
        var probe = MakeProbe(handler, OpenAiConfig());
        Assert.Equal(VendorCapabilityState.Supported, await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs));
    }

    [Fact]
    public async Task OpenAi_200ButInvalidJsonContent_StructuredOutputs_Missing()
    {
        // 200 但响应不是 schema 化 JSON → 不可宣称支持（fail-closed）。
        var handler = new FakeHandler(Json(200, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"plain text\"}}]}"));
        var probe = MakeProbe(handler, OpenAiConfig());
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs));
    }

    [Fact]
    public async Task Anthropic_ForcedToolUseOk_StructuredOutputs_Supported()
    {
        var handler = new FakeHandler(Json(200, "{\"content\":[{\"type\":\"tool_use\",\"name\":\"probe_report\",\"input\":{\"ok\":true}}]}"));
        var probe = MakeProbe(handler, AnthropicConfig());
        Assert.Equal(VendorCapabilityState.Supported, await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs));
    }

    [Fact]
    public async Task Anthropic_ToolRejected400_StructuredOutputs_Missing()
    {
        var handler = new FakeHandler(Json(400, "{\"error\":\"tool_choice unsupported\"}"));
        var probe = MakeProbe(handler, AnthropicConfig());
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs));
    }

    [Fact]
    public async Task Gemini_ProbeOk_StructuredOutputs_Downgraded()
    {
        // G 的文档化降级路径：prompt-JSON + 校验 → Downgraded。
        var handler = new FakeHandler(Json(200, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"ok\\\":true}\"}]}}]}"));
        var probe = MakeProbe(handler, GeminiConfig());
        Assert.Equal(VendorCapabilityState.Downgraded, await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs));
    }

    [Fact]
    public async Task Gemini_ProbeRejected400_StructuredOutputs_Missing()
    {
        var handler = new FakeHandler(Json(400, "{\"error\":\"bad\"}"));
        var probe = MakeProbe(handler, GeminiConfig());
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs));
    }

    // ---------- 探测请求不携带用户对话数据 ----------

    [Fact]
    public async Task ProbeRequests_CarryNoUserData_FixedConstantsOnly()
    {
        // 配置里的 DisplayName 埋 canary（非探测必需字段）；探测体必须是固定常量。
        var handler = new FakeHandler(Json(200, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"ok\\\":true}\"}}]}"));
        var probe = MakeProbe(handler, OpenAiConfig());
        await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs);
        await probe.ProbeAsync("p1", VendorCapability.PromptCache);
        var body = handler.LastRequestBody ?? "";
        Assert.Contains("ping", body);
        Assert.DoesNotContain(UserCanary, body);
        Assert.DoesNotContain(UserCanary, handler.LastRequestUrl ?? "");
    }

    // ---------- EffortTiers：配置 + 模型 id 依据（无外呼） ----------

    [Fact]
    public async Task EffortTiers_ConfiguredThinkingEfforts_SupportedWithZeroHttpRequests()
    {
        var cfg = OpenAiConfig("some-plain-model");
        cfg.ThinkingEfforts = "low,medium,high,max";
        var handler = new FakeHandler(Json(200, "{}"));
        var probe = MakeProbe(handler, cfg);
        Assert.Equal(VendorCapabilityState.Supported, await probe.ProbeAsync("p1", VendorCapability.EffortTiers));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task EffortTiers_ReasoningModelId_Supported()
    {
        var handler = new FakeHandler(Json(200, "{}"));
        var probe = MakeProbe(handler, OpenAiConfig("gpt-5.2"));
        Assert.Equal(VendorCapabilityState.Supported, await probe.ProbeAsync("p1", VendorCapability.EffortTiers));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task EffortTiers_PlainModelNoConfig_Missing()
    {
        var handler = new FakeHandler(Json(200, "{}"));
        var probe = MakeProbe(handler, OpenAiConfig("text-embed-3"));
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.EffortTiers));
    }

    // ---------- PromptCache ----------

    [Fact]
    public async Task Anthropic_CacheControlAccepted_PromptCache_Supported()
    {
        var handler = new FakeHandler(Json(200, "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}"));
        var probe = MakeProbe(handler, AnthropicConfig());
        Assert.Equal(VendorCapabilityState.Supported, await probe.ProbeAsync("p1", VendorCapability.PromptCache));
    }

    [Fact]
    public async Task Anthropic_CacheControlRejected_PromptCache_Missing()
    {
        var handler = new FakeHandler(Json(400, "{\"error\":\"cache_control invalid\"}"));
        var probe = MakeProbe(handler, AnthropicConfig());
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.PromptCache));
    }

    [Fact]
    public async Task OpenAi_IneligibleModel_PromptCache_MissingWithZeroHttpRequests()
    {
        var handler = new FakeHandler(Json(200, "{}"));
        var probe = MakeProbe(handler, OpenAiConfig("davinci-002"));
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.PromptCache));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task OpenAi_EligibleModelProbeOk_PromptCache_Supported()
    {
        var handler = new FakeHandler(Json(200, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}"));
        var probe = MakeProbe(handler, OpenAiConfig("gpt-4o-mini"));
        Assert.Equal(VendorCapabilityState.Supported, await probe.ProbeAsync("p1", VendorCapability.PromptCache));
    }

    // ---------- Batch ----------

    [Fact]
    public async Task Batch_NonOfficialEndpoint_MissingWithZeroHttpRequests()
    {
        var cfg = OpenAiConfig();
        cfg.BaseUrl = "https://api.deepseek.com/v1";
        var handler = new FakeHandler(Json(200, "[]"));
        var probe = MakeProbe(handler, cfg);
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.Batch));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Batch_OpenAiOfficial_ListEndpointOk_Supported()
    {
        var handler = new FakeHandler(Json(200, "[]"));
        var probe = MakeProbe(handler, OpenAiConfig());
        Assert.Equal(VendorCapabilityState.Supported, await probe.ProbeAsync("p1", VendorCapability.Batch));
        Assert.Contains("/batches", handler.LastRequestUrl);
        // 只读 GET 探测，不携带用户数据。
        Assert.DoesNotContain(UserCanary, handler.LastRequestUrl ?? "");
    }

    [Fact]
    public async Task Batch_AnthropicOfficial_ListEndpointOk_Supported()
    {
        var handler = new FakeHandler(Json(200, "{\"data\":[]}"));
        var probe = MakeProbe(handler, AnthropicConfig());
        Assert.Equal(VendorCapabilityState.Supported, await probe.ProbeAsync("p1", VendorCapability.Batch));
        Assert.Contains("/v1/messages/batches", handler.LastRequestUrl);
    }

    [Fact]
    public async Task Batch_Endpoint404_Missing()
    {
        var handler = new FakeHandler(Json(404, "{\"error\":\"not found\"}"));
        var probe = MakeProbe(handler, OpenAiConfig());
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.Batch));
    }

    // ---------- 认证缺失 fail-closed ----------

    [Fact]
    public async Task RequiredKeyMissing_NetworkProbes_Missing()
    {
        var cfg = OpenAiConfig();
        cfg.RequiresApiKey = true;
        cfg.ApiKeyEnvVar = "BETA_PROBE_DEFINITELY_UNSET_ENV";
        var handler = new FakeHandler(Json(200, "{}"));
        var probe = MakeProbe(handler, cfg);
        Assert.Equal(VendorCapabilityState.Missing, await probe.ProbeAsync("p1", VendorCapability.StructuredOutputs));
        Assert.Equal(0, handler.RequestCount);
    }
}
