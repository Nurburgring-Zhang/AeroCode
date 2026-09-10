// Copyright (c) AeroCode
// MiniMax vision 真实 E2E：仅当 MINIMAX_API_KEY 存在时运行（否则跳过，不拖累常规套件）。
// 验证 MiniMaxProvider（SupportsVision）把图像以 content-parts 真实上送 MiniMax-M3 并得到描述。
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroCode.Tests.ServiceTests;

public sealed class MiniMaxVisionLiveTests
{
    // 120x120 白底红色圆形 PNG（615 字节）。
    private const string RedCirclePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAHgAAAB4CAIAAAC2BqGFAAACLklEQVR4nO3dQU4DMQxA0emIy3AC33+ZE/Q4Zce60tg/"
        + "cfL/GqnxwwpVEczr8/lcVt8NvIYJzSU0lNBQQkMJDSU0lNBQQkMJDSU0lNBQQkMJDSU0lNBQQkMJDSU01M+1fOP395svi/"
        + "f7WrjXmr+cHd/hNkJfC3o8811ZfAnoke27oPhk6FFPvAj3NOjBEk/nvk9Tvia9Or3RYyrxxNW+j1W+2PPcxyrDpyKujrEkMXy"
        + "NlG90C+Wr/py10F2UgdMWQvdSrj6zH5NCVUF3XOfSk5dA91WuO38+dHfloim8o6GSofdY54pZMqF3Uk6fyKsDKg16v3XOncuNhsqB3nWdE6dzo6GE7gO9972RNaMbDSV0E+gT7o2USd1oKKGhhO4Afc4F/"
        + "XxeNxpKaCihoYSGEhpKaCihoYSGEhpKaCihoYTuAD39D6zhnszrRkMJDSV0E+hzrul4NqkbDSV0H+gTbo94PKMbDSV0K+i9b4/"
        + "ImM6NhkqD3nWpI2kuNxoqE3q/pY68iZI3eifrSJ3FqwMqH3qPpY7sKUo2urt1FJy/6uroax01J/eOhiqE7rjUUXbm2o3uZR2Vpy2/"
        + "OrpYR/E5uf8fPVb9yyJmFbgfhrHkamOnQt91xGLW5HnmPExhzL5G+G/5nPfRMXW1p7y6z2GB8slCJ0H/57Oy6IZPf+MbPs/Qvs+PSaGEhhIaSmgooaGEhhIaSmgooaGEhhIaSmgooaGEhhIaSmgooaGEvpj+ADfpvbM6d8CDAAAAAElFTkSuQmCC";

    private static string? Key() => Environment.GetEnvironmentVariable("MINIMAX_API_KEY");

    [SkippableFact]
    public async Task MiniMax_Vision_RealE2E_DescribesImage()
    {
        var key = Key();
        Skip.If(string.IsNullOrWhiteSpace(key), "未设置 MINIMAX_API_KEY——跳过真实 vision E2E（非失败）");

        // 图像落临时文件 → MessageAttachment(SourcePath) → BuildVisionImages 走与生产一致的抽取路径。
        var imgPath = Path.Combine(Path.GetTempPath(), $"aero_mm_vision_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imgPath, Convert.FromBase64String(RedCirclePngBase64));
        try
        {
            var attachment = new MessageAttachment("redcircle.png", "image/png", new FileInfo(imgPath).Length)
            {
                SourcePath = imgPath,
            };
            var images = MessageAttachment.BuildVisionImages(new[] { attachment });
            Assert.NotEmpty(images); // 抽取成功（文件可达、在预算内）

            var config = new ProviderConfig
            {
                Id = "minimax",
                DisplayName = "MiniMax",
                Kind = "OpenAICompatible",
                BaseUrl = "https://api.minimax.chat/v1",
                DefaultModel = "MiniMax-M3",
                ApiKeyEnvVar = "MINIMAX_API_KEY",
                RequiresApiKey = true,
                SupportsStreaming = false,
                SupportsVision = true,
                TimeoutSeconds = 90,
            };
            using var http = new HttpClient();
            var provider = new MiniMaxProvider(http, config, NullLogger<MiniMaxProvider>.Instance);
            Assert.True(provider.SupportsVision);

            var request = new ChatRequest
            {
                Model = "MiniMax-M3",
                Stream = false,
                EnableThinking = false,
                Messages = new[]
                {
                    new AiChatMessage
                    {
                        Role = "user",
                        Content = "What shape and color do you see in this image? Answer briefly.",
                        Images = images,
                    },
                },
            };

            var response = await provider.ChatAsync(request);

            // 真实返回非空内容即证明 MiniMax-M3 接受了图像输入并经 content-parts 上送成功。
            Assert.False(string.IsNullOrWhiteSpace(response.Content), "vision 请求未返回内容");

            // 进一步证明模型「看到」了图：回复应提及红色/圆形/橙色等形状颜色特征。
            var c = response.Content.ToLowerInvariant();
            Assert.True(
                c.Contains("red") || c.Contains("circle") || c.Contains("round")
                || c.Contains("orange") || c.Contains("红") || c.Contains("圆"),
                $"回复未体现对图像的理解：{response.Content}");
        }
        finally
        {
            try { File.Delete(imgPath); } catch { /* best effort */ }
        }
    }
}
