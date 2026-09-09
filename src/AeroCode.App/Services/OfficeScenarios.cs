// Copyright (c) AeroCode
// OfficeScenarios — UIR-3：100 个具体办公/生产/工作/学习场景模板库。
// 每个场景 = 分类 + 名称 + 提示词模板（{input} 为占位符）。数据驱动，真实 LLM 执行。
using System.Collections.Generic;
using System.Linq;

namespace AeroCode.App.Services;

/// <summary>一个办公/生产/学习场景模板。</summary>
public sealed record ScenarioTemplate(string Category, string Name, string Prompt)
{
    /// <summary>显示名（分类 · 名称）。</summary>
    public string Display => $"{Category} · {Name}";
}

/// <summary>
/// 100 个办公/生产/工作/学习场景库。提示词模板以 {input} 标记用户输入占位。
/// </summary>
public static class OfficeScenarios
{
    public static IReadOnlyList<ScenarioTemplate> All { get; } = BuildAll();

    public static IReadOnlyList<string> Categories =>
        All.Select(s => s.Category).Distinct().ToList();

    private static IReadOnlyList<ScenarioTemplate> BuildAll()
    {
        var list = new List<ScenarioTemplate>(100);

        // ── 写作润色（13）──
        list.Add(new("写作润色", "润色改写", "你是资深编辑。请润色下面的文字，使其更流畅专业，保留原意：\n{input}"));
        list.Add(new("写作润色", "精简压缩", "把下面的文字压缩到原长度的 1/3，保留核心信息：\n{input}"));
        list.Add(new("写作润色", "扩写充实", "在保持原意的前提下扩写下面的内容，补充细节与论据：\n{input}"));
        list.Add(new("写作润色", "纠错别字", "找出下面文字中的错别字、语病和标点错误，逐条列出并给出修正：\n{input}"));
        list.Add(new("写作润色", "语气转正式", "把下面的口语化内容改写为正式书面语：\n{input}"));
        list.Add(new("写作润色", "语气转亲和", "把下面的正式文本改写为更亲和、口语化的表达：\n{input}"));
        list.Add(new("写作润色", "续写下文", "顺着下面内容的思路和风格续写一段：\n{input}"));
        list.Add(new("写作润色", "起标题", "为下面的内容拟 5 个吸引人的标题（含 1 个正式、1 个新颖）：\n{input}"));
        list.Add(new("写作润色", "写摘要", "为下面的长文写一段 100 字以内的摘要：\n{input}"));
        list.Add(new("写作润色", "提取关键词", "从下面的内容中提取 8 个最关键的词/短语：\n{input}"));
        list.Add(new("写作润色", "改写为第一人称", "把下面的内容改写为第一人称叙述：\n{input}"));
        list.Add(new("写作润色", "改写为第三人称", "把下面的内容改写为第三人称客观叙述：\n{input}"));
        list.Add(new("写作润色", "风格模仿", "模仿简洁干练的写作风格重写下面内容：\n{input}"));

        // ── 分析总结（13）──
        list.Add(new("分析总结", "提炼要点", "提炼下面内容的 5 个核心要点，分条列出：\n{input}"));
        list.Add(new("分析总结", "结构化大纲", "把下面的内容整理为层级清晰的 Markdown 大纲：\n{input}"));
        list.Add(new("分析总结", "SWOT 分析", "对下面的主题做 SWOT 分析（优势/劣势/机会/威胁）：\n{input}"));
        list.Add(new("分析总结", "利弊分析", "分析下面方案/做法的优点与缺点，并给出权衡建议：\n{input}"));
        list.Add(new("分析总结", "根因分析", "针对下面描述的问题做根因分析（可用 5-Why 法）：\n{input}"));
        list.Add(new("分析总结", "对比分析", "对下面提到的两个对象做逐项对比分析：\n{input}"));
        list.Add(new("分析总结", "提炼结论", "从下面的论述中提炼出明确结论与支撑依据：\n{input}"));
        list.Add(new("分析总结", "假设检验", "审视下面的观点，列出其隐含假设并逐条检验是否成立：\n{input}"));
        list.Add(new("分析总结", "风险识别", "识别下面计划/方案中的潜在风险并给出应对建议：\n{input}"));
        list.Add(new("分析总结", "趋势推断", "基于下面的信息推断可能的发展趋势与关键变量：\n{input}"));
        list.Add(new("分析总结", "一句话总结", "用一句话概括下面内容的核心：\n{input}"));
        list.Add(new("分析总结", "分层归纳", "把下面的零散信息按主题分层归纳：\n{input}"));
        list.Add(new("分析总结", "批判性审视", "以批判性思维审视下面的论证，指出逻辑漏洞：\n{input}"));

        // ── 办公文档（13）──
        list.Add(new("办公文档", "写周报", "根据下面的工作内容写一份结构化周报（本周完成/下周计划/风险）：\n{input}"));
        list.Add(new("办公文档", "写月报", "根据下面的内容写一份月度工作总结：\n{input}"));
        list.Add(new("办公文档", "写会议纪要", "把下面的会议记录整理为规范会议纪要（决议/负责人/时限）：\n{input}"));
        list.Add(new("办公文档", "写邮件", "根据下面的要点起草一封得体的职场邮件：\n{input}"));
        list.Add(new("办公文档", "写通知", "根据下面的信息写一则清晰的通知/公告：\n{input}"));
        list.Add(new("办公文档", "写汇报 PPT 大纲", "为下面的主题生成汇报 PPT 的页级大纲：\n{input}"));
        list.Add(new("办公文档", "写项目方案", "根据下面的背景写一份项目方案（目标/方案/里程碑/资源）：\n{input}"));
        list.Add(new("办公文档", "写需求文档", "把下面的需求描述整理为结构化需求文档：\n{input}"));
        list.Add(new("办公文档", "写复盘报告", "根据下面的情况写一份复盘报告（目标/结果/差异/改进）：\n{input}"));
        list.Add(new("办公文档", "待办提取", "从下面的内容中提取所有待办事项，输出任务清单：\n{input}"));
        list.Add(new("办公文档", "写 OKR", "根据下面的目标写一组 OKR（O + 可量化 KR）：\n{input}"));
        list.Add(new("办公文档", "写述职提纲", "根据下面的业绩写一份述职提纲：\n{input}"));
        list.Add(new("办公文档", "表格化整理", "把下面的信息整理成结构合理的 Markdown 表格：\n{input}"));

        // ── 编程开发（13）──
        list.Add(new("编程开发", "解释代码", "逐段解释下面代码的作用与关键逻辑：\n{input}"));
        list.Add(new("编程开发", "找 Bug", "审查下面的代码，找出潜在 bug 与边界问题：\n{input}"));
        list.Add(new("编程开发", "代码重构", "重构下面的代码以提高可读性/可维护性，说明改动理由：\n{input}"));
        list.Add(new("编程开发", "写单元测试", "为下面的代码编写单元测试（覆盖正常与边界）：\n{input}"));
        list.Add(new("编程开发", "性能优化", "分析下面代码的性能瓶颈并给出优化方案：\n{input}"));
        list.Add(new("编程开发", "写注释文档", "为下面的代码补充清晰的注释与文档说明：\n{input}"));
        list.Add(new("编程开发", "代码转伪代码", "把下面的代码转写为清晰的伪代码/流程描述：\n{input}"));
        list.Add(new("编程开发", "写正则", "根据下面的需求编写正则表达式并解释各部分：\n{input}"));
        list.Add(new("编程开发", "写 SQL", "根据下面的需求编写 SQL 语句并解释：\n{input}"));
        list.Add(new("编程开发", "安全审查", "审查下面代码的安全隐患（注入/越权/泄露等）：\n{input}"));
        list.Add(new("编程开发", "命名建议", "为下面的变量/函数/类给出更语义化的命名建议：\n{input}"));
        list.Add(new("编程开发", "写提交信息", "根据下面的改动描述写规范的 git commit message：\n{input}"));
        list.Add(new("编程开发", "技术选型建议", "针对下面的场景给出技术选型建议与权衡：\n{input}"));

        // ── 学习辅导（13）──
        list.Add(new("学习辅导", "概念讲解", "用通俗易懂的方式讲解下面的概念/知识点：\n{input}"));
        list.Add(new("学习辅导", "费曼式解释", "用费曼学习法（讲给外行听）解释下面的内容：\n{input}"));
        list.Add(new("学习辅导", "出练习题", "根据下面的知识点出 5 道练习题并附答案：\n{input}"));
        list.Add(new("学习辅导", "制定学习计划", "根据下面的学习目标制定分阶段学习计划：\n{input}"));
        list.Add(new("学习辅导", "类比理解", "用生活中的类比帮助理解下面的抽象概念：\n{input}"));
        list.Add(new("学习辅导", "知识脉络", "梳理下面知识点的脉络与前后关联：\n{input}"));
        list.Add(new("学习辅导", "常见误区", "列出学习下面主题时的常见误区并纠正：\n{input}"));
        list.Add(new("学习辅导", "苏格拉底提问", "针对下面的观点提出引导深入思考的问题：\n{input}"));
        list.Add(new("学习辅导", "记忆口诀", "为下面的知识点编写便于记忆的口诀/助记：\n{input}"));
        list.Add(new("学习辅导", "由浅入深", "把下面的主题按由浅入深的顺序拆解讲解：\n{input}"));
        list.Add(new("学习辅导", "举例说明", "为下面的理论/概念给出多个具体例子：\n{input}"));
        list.Add(new("学习辅导", "对比辨析", "辨析下面两个易混淆概念的异同：\n{input}"));
        list.Add(new("学习辅导", "复述检测", "根据下面的内容出题检测我是否真正理解：\n{input}"));

        // ── 数据处理（12）──
        list.Add(new("数据处理", "数据解读", "解读下面的数据/指标，给出洞察与结论：\n{input}"));
        list.Add(new("数据处理", "异常识别", "检查下面的数据，找出异常值并分析可能原因：\n{input}"));
        list.Add(new("数据处理", "数据清洗建议", "针对下面的数据问题给出清洗/规范化建议：\n{input}"));
        list.Add(new("数据处理", "写数据处理脚本", "根据下面的需求写一段数据处理脚本（注明语言）：\n{input}"));
        list.Add(new("数据处理", "指标口径梳理", "梳理下面指标的定义与计算口径：\n{input}"));
        list.Add(new("数据处理", "趋势描述", "用准确的语言描述下面数据的趋势变化：\n{input}"));
        list.Add(new("数据处理", "维度拆解", "建议从哪些维度拆解分析下面的数据：\n{input}"));
        list.Add(new("数据处理", "数据可视化建议", "为下面的数据推荐合适的图表类型并说明理由：\n{input}"));
        list.Add(new("数据处理", "同环比分析", "对下面的数据做同比/环比分析：\n{input}"));
        list.Add(new("数据处理", "写数据字典", "为下面的字段列表编写数据字典说明：\n{input}"));
        list.Add(new("数据处理", "归因分析", "对下面的指标变化做归因分析：\n{input}"));
        list.Add(new("数据处理", "数据汇报", "把下面的数据整理为面向管理层的汇报要点：\n{input}"));

        // ── 沟通表达（12）──
        list.Add(new("沟通表达", "委婉表达", "把下面的话说得更委婉得体：\n{input}"));
        list.Add(new("沟通表达", "直接表达", "把下面含糊的表达改写为清晰直接的表述：\n{input}"));
        list.Add(new("沟通表达", "说服话术", "为下面的诉求撰写有说服力的表达：\n{input}"));
        list.Add(new("沟通表达", "拒绝话术", "为下面的请求写一段礼貌而坚定的拒绝：\n{input}"));
        list.Add(new("沟通表达", "道歉话术", "为下面的情况写一段诚恳的道歉：\n{input}"));
        list.Add(new("沟通表达", "向上汇报", "把下面的内容组织为面向上级的简明汇报：\n{input}"));
        list.Add(new("沟通表达", "跨部门沟通", "为下面的协作事项起草跨部门沟通要点：\n{input}"));
        list.Add(new("沟通表达", "答疑话术", "针对下面的问题准备得体的回答：\n{input}"));
        list.Add(new("沟通表达", "谈判要点", "为下面的谈判场景准备要点与底线：\n{input}"));
        list.Add(new("沟通表达", "反馈话术", "为下面的情况写一段建设性反馈：\n{input}"));
        list.Add(new("沟通表达", " Elevator Pitch", "把下面的想法浓缩为 30 秒电梯陈述：\n{input}"));
        list.Add(new("沟通表达", "提问清单", "为下面的主题准备高质量提问清单：\n{input}"));

        // ── 策划创意（11）──
        list.Add(new("策划创意", "头脑风暴", "围绕下面的主题做发散构思，给出 10 个点子：\n{input}"));
        list.Add(new("策划创意", "活动策划", "为下面的目标策划一份活动方案（流程/物料/预算）：\n{input}"));
        list.Add(new("策划创意", "营销文案", "为下面的产品/活动写营销文案：\n{input}"));
        list.Add(new("策划创意", "起名字", "为下面的事物起 10 个名字并说明寓意：\n{input}"));
        list.Add(new("策划创意", "Slogan", "为下面的主题创作 5 句 slogan：\n{input}"));
        list.Add(new("策划创意", "内容选题", "围绕下面的领域给出 10 个内容选题：\n{input}"));
        list.Add(new("策划创意", "脚本创作", "根据下面的主题写一段短视频脚本：\n{input}"));
        list.Add(new("策划创意", "故事化表达", "把下面的内容改写为有故事感的叙述：\n{input}"));
        list.Add(new("策划创意", "开场白", "为下面的场合写一段吸引人的开场白：\n{input}"));
        list.Add(new("策划创意", "结尾升华", "为下面的内容写一个有力的结尾：\n{input}"));
        list.Add(new("策划创意", "创意评估", "评估下面的创意的可行性与改进空间：\n{input}"));

        // ── 决策规划（10）──
        list.Add(new("决策规划", "决策矩阵", "用决策矩阵帮助评估下面的几个选项：\n{input}"));
        list.Add(new("决策规划", "目标拆解", "把下面的大目标拆解为可执行的小目标：\n{input}"));
        list.Add(new("决策规划", "制定计划", "为下面的目标制定行动计划（步骤/时间/负责）：\n{input}"));
        list.Add(new("决策规划", "优先级排序", "对下面的任务做优先级排序并说明依据：\n{input}"));
        list.Add(new("决策规划", "时间估算", "估算下面各项任务所需时间并给出排期建议：\n{input}"));
        list.Add(new("决策规划", "里程碑规划", "为下面的项目规划关键里程碑：\n{input}"));
        list.Add(new("决策规划", "二阶思考", "对下面的决策做二阶后果推演：\n{input}"));
        list.Add(new("决策规划", "机会成本", "分析下面选择的机会成本：\n{input}"));
        list.Add(new("决策规划", "复盘提问", "为下面的结果准备复盘提问清单：\n{input}"));
        list.Add(new("决策规划", "预案设计", "为下面的计划设计风险预案（Plan B）：\n{input}"));

        return list;
    }
}
