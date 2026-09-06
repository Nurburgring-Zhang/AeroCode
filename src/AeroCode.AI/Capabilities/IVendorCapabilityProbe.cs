using System.Threading;
using System.Threading.Tasks;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// 厂商能力运行时探测契约（wave2 R2 钉死签名；R3-δ 契约 <b>追加</b>可选 <see cref="CancellationToken"/>
/// 参数——默认值保持源兼容，既有调用点/实现只需按需演进）。实现必须基于 provider 配置 /
/// 模型 id / 探测响应做运行时判断，禁止静态宣称支持；探测失败一律返回 Missing（fail-closed）。
/// </summary>
public interface IVendorCapabilityProbe
{
    /// <summary>
    /// 探测指定 provider 对指定能力的实际支持状态。
    /// 探测请求不得携带用户对话数据。<paramref name="ct"/> 取消时实现应尽快收敛（最终仍 fail-closed）。
    /// </summary>
    Task<VendorCapabilityState> ProbeAsync(string providerId, VendorCapability capability, CancellationToken ct = default);

    /// <summary>
    /// R3-δ 可选扩展（default interface method，未实现 = null）：返回该 provider × capability 的
    /// 文档化降级原因（供 capability 矩阵 Downgraded 格落的 reason 字段）。仅探测实现自知的
    /// 文档化降级路径返回非空；不适用/未知/任何异常一律 null（矩阵端绝不伪造 reason）。
    /// </summary>
    string? DescribeDowngrade(string providerId, VendorCapability capability) => null;
}
