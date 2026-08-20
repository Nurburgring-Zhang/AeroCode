namespace AeroAgent.Autonomy.Common;

/// <summary>
/// 产出来源标注：区分确定性启发式与真实 LLM 生成。
/// 自主内核的诚实性要求——任何 LLM 增强失败退回启发式时，来源必须如实为 Heuristic。
/// </summary>
public enum AnalysisSource
{
    /// <summary>确定性启发式（关键词/结构特征规则）。</summary>
    Heuristic = 0,

    /// <summary>真实 LLM 调用产出（解析校验通过）。</summary>
    Llm = 1,
}
