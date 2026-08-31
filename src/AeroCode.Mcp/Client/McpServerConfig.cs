using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AeroCode.Mcp.Client;

/// <summary>
/// 单个 MCP server 的连接配置（settings.json 的 mcpServers 段）。
/// stdio 传输：Command + Arguments 拉起子进程（如 dotnet exec aerocode-mcp.dll）。
/// 序列化属性名显式声明——settings.json 读取走默认反序列化（不带命名策略）。
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>唯一标识：工具名加此前缀防跨服务器冲突（非法字符会被清洗为下划线）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>可执行文件（如 "dotnet"、"node"、绝对路径）。</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>启动参数（如 ["exec", "…/aerocode-mcp.dll"]）。</summary>
    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();

    /// <summary>
    /// 子进程环境变量（合并到继承环境之上）。值支持 <c>${ENV_NAME}</c> 引用——
    /// 启动时从当前进程环境展开，API key 等敏感值因此不必明文写进 settings.json；
    /// 字面量值继续原样传递（向后兼容）。引用未解析时子进程该变量为未设置并大声告警。
    /// </summary>
    [JsonPropertyName("environmentVariables")]
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>工作目录（可空 = 继承）。</summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    // ---- 远程传输扩展（批次 B G4，builder-γ）：Url 非空 = 远程服务器，command 仅在 stdio 时需要 ----

    /// <summary>
    /// 远程服务器地址（绝对 http/https）。非空 = 走 HTTP 传输（SSE 或 Streamable HTTP），
    /// Command/Arguments 被忽略；空 = 既有 stdio 子进程行为（向后兼容）。
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// 传输种类（仅 url 配置时生效）："streamableHttp"（缺省，当前 MCP 标准）| "sse"（旧式 HTTP+SSE）。
    /// 注意带 headers/token 的远程服务器凭据：header 值支持 ${ENV_NAME} 引用（同 environmentVariables 语义），
    /// 或经 <see cref="ITokenProvider"/> 注入 Authorization——都绝不落盘。
    /// </summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    /// <summary>远程请求附带头（如 X-Api-Key）。值支持 ${ENV_NAME} 引用展开；引用未设置时该头被省略并告警。</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>false = 配置保留但当前不连接（UI 可一键停用）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}
