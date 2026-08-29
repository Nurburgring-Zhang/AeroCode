// Copyright (c) AeroCode V3.0
// Permission persistence — user tool-authorization decisions (permissions.json).
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroCode.Harness.Permission;

/// <summary>
/// 权限决策持久化内容：每个工具一条用户决策（授权对话框"记住选择"与设置页修改的落盘结果）。
/// Key = 工具名；Value = 该工具今后的默认决策。三种决策都可持久化——
/// Ask 表示"每次都再问我"，用户可主动把任何工具恢复成询问态。
/// </summary>
public sealed class PermissionSettings
{
    public Dictionary<string, PermissionDecision> ToolDecisions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 权限决策的 JSON 文件存储。与 MOA 选项存储同一策略：
/// 原子写（随机临时文件 + Move 覆盖）、缺失/损坏回退空配置不阻塞启动。
/// 枚举按字符串序列化——permissions.json 是用户可能直接查看的配置文件。
/// </summary>
public sealed class JsonPermissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        // permissions.json 是用户可能直接查看的配置文件：工具名/决策保持可读。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    // 并发写闸门：MOA 多 worker 的弹窗回调与设置页保存可能并发 SaveAsync；
    // 串行化保证"写临时文件 → Move 覆盖"两步作为整体完成，不留孤儿 .tmp。
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public JsonPermissionStore(string filePath)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? throw new ArgumentException("path required", nameof(filePath))
            : filePath;
    }

    /// <summary>读取持久化决策；文件缺失或损坏时返回空配置（诚实回退，不抛异常阻塞启动）。</summary>
    public async Task<PermissionSettings> LoadAsync(CancellationToken ct = default)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // 文件缺失、或 Exists→Read 之间被删除的竞态：与"文件缺失"同义，
            // 按契约回退空配置（不抛异常阻塞启动）。
            return new PermissionSettings();
        }
        catch (DirectoryNotFoundException)
        {
            // 父目录尚不存在同样等价于"文件缺失"（ReadAllText 对不存在目录抛此异常）。
            return new PermissionSettings();
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new PermissionSettings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<PermissionSettings>(json, JsonOptions)
                         ?? new PermissionSettings();
            // 显式 {"ToolDecisions":null} 是合法 JSON（不触发 JsonException），必须兜底，
            // 否则启动期遍历会 NRE——配置损坏绝不阻塞启动。
            loaded.ToolDecisions ??= new Dictionary<string, PermissionDecision>(StringComparer.Ordinal);
            return loaded;
        }
        catch (JsonException)
        {
            // 配置损坏不应阻塞启动：返回空配置，由用户重新保存覆盖。
            return new PermissionSettings();
        }
    }

    /// <summary>
    /// 原子写入：先写随机临时文件再 Move 覆盖，避免半截文件；并发写串行化。
    /// 写入被取消或 Move 失败时，尽力清理孤儿 .tmp（清理本身的失败不掩盖原异常）。
    /// </summary>
    public async Task SaveAsync(PermissionSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            tmp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // 写中取消 / Move 失败：尽力清掉孤儿 .tmp，再原样重抛保留失败语义。
            if (tmp is not null)
            {
                try { File.Delete(tmp); }
                catch { /* 尽力清理：二次失败不掩盖原异常 */ }
            }
            throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
