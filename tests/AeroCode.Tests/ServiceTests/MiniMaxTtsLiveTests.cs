// Copyright (c) AeroCode
// MiniMax TTS 真实 E2E：仅当 MINIMAX_API_KEY 存在时运行（否则跳过，不拖累常规套件）。
// 验证 MiniMaxMultimodalClient.GenerateSpeechAsync（t2a_v2, speech-01-turbo）返回真实音频字节。
using System;
using System.Threading.Tasks;
using AeroCode.AI.Multimodal;
using Xunit;

namespace AeroCode.Tests.ServiceTests;

public sealed class MiniMaxTtsLiveTests
{
    private static string? Key() => Environment.GetEnvironmentVariable("MINIMAX_API_KEY");

    [SkippableFact]
    public async Task MiniMax_Tts_RealE2E_ProducesAudioBytes()
    {
        var key = Key();
        Skip.If(string.IsNullOrWhiteSpace(key), "未设置 MINIMAX_API_KEY——跳过真实 TTS E2E（非失败）");

        var client = new MiniMaxMultimodalClient();
        var result = await client.GenerateSpeechAsync("你好，这是 AeroCode 语音合成的真实端到端测试。");

        // 真实返回非空音频字节即证明 t2a_v2 接入成功（speech-01-turbo + female-shaonv 已实证）。
        Assert.NotNull(result.AudioBytes);
        Assert.True(result.AudioBytes.Length > 1000, $"音频字节过少，疑似无效：{result.AudioBytes.Length}");
    }
}
