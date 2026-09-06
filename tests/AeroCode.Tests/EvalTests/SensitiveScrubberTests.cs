using AeroCode.Eval;
using Xunit;

namespace AeroCode.Tests.EvalTests;

/// <summary>
/// 安全硬验收：网关密钥/凭据不得出现在报告、日志、异常消息。
/// SensitiveScrubber 统一脱敏：已注册凭据原文精确替换 + sk-*/Bearer/api_key= 形态兜底。
/// </summary>
public sealed class SensitiveScrubberTests
{
    [Fact]
    public void Scrub_ReplacesRegisteredSecretLiterals()
    {
        var scrubber = new SensitiveScrubber(["sk-live-abcdef1234567890", "hunter2secret"]);

        var scrubbed = scrubber.Scrub(
            "POST /v1/moa/execute Authorization: Bearer sk-live-abcdef1234567890 done, fallback=hunter2secret");

        Assert.DoesNotContain("sk-live-abcdef1234567890", scrubbed);
        Assert.DoesNotContain("hunter2secret", scrubbed);
        Assert.Contains("[REDACTED]", scrubbed);
        Assert.Contains("POST /v1/moa/execute", scrubbed);
    }

    [Fact]
    public void Scrub_RedactsUnregisteredSkShapedKeys()
    {
        var scrubber = new SensitiveScrubber(Array.Empty<string?>());

        var scrubbed = scrubber.Scrub("connection failed with key=sk-unknownvalue123456; retrying");

        Assert.DoesNotContain("sk-unknownvalue123456", scrubbed);
        Assert.Contains("[REDACTED]", scrubbed);
    }

    [Fact]
    public void ScrubException_ScrubsMessageAndStack()
    {
        var scrubber = new SensitiveScrubber(["supersecretvalue12345"]);
        Exception caught;
        try
        {
            throw new InvalidOperationException("gateway rejected token=supersecretvalue12345");
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        var text = scrubber.ScrubException(caught);

        Assert.DoesNotContain("supersecretvalue12345", text);
        Assert.Contains("[REDACTED]", text);
        Assert.Contains("InvalidOperationException", text);
    }

    [Fact]
    public void CollectCandidateSecrets_PicksUpEvalCredentialVariable()
    {
        const string probeKey = "sk-probe-e2e-abcdef123456";
        Environment.SetEnvironmentVariable(EvalSecrets.GatewayKeyVariable, probeKey);
        try
        {
            var secrets = EvalSecrets.CollectCandidateSecrets();

            Assert.Contains(probeKey, secrets);
            var scrubber = new SensitiveScrubber(secrets);
            Assert.DoesNotContain(probeKey, scrubber.Scrub($"request used AEROCODE_EVAL_GATEWAY_KEY={probeKey}"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalSecrets.GatewayKeyVariable, null);
        }
    }

    [Fact]
    public void CollectCandidateSecrets_IgnoresNonCredentialEvalVariables()
    {
        const string url = "http://127.0.0.1:8910";
        Environment.SetEnvironmentVariable(EvalSecrets.GatewayUrlVariable, url);
        try
        {
            Assert.DoesNotContain(url, EvalSecrets.CollectCandidateSecrets());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalSecrets.GatewayUrlVariable, null);
        }
    }
}
