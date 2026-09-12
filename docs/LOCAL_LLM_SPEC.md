# AeroCode 本地大模型子系统 · 完整规格（LOCAL_LLM_SPEC）

> 版本：v1.0 · 2026-09-12
> 目标：让 AeroCode 能**加载并运行本地大模型**（GGUF 优先，safetensors 次之），支持参数调节、运行时检测与更新，并尽可能接入加速技术。
> **诚实基线**：本规格以本机实测环境为约束，凡本机无法验证的能力一律标注 `[环境受限]`，绝不写"假装支持"的代码。

---

## 0. 本机环境实测结论（硬事实，2026-09-12 探测）

| 项 | 实测结果 | 对方案的影响 |
|---|---|---|
| GPU | **Intel Iris Xe（集显，~1GB 共享）**，`nvidia-smi` 不存在 | **无 NVIDIA GPU → CUDA / FlashAttention / TensorRT-LLM / vLLM / ExLlamaV2 / AWQ/GPTQ GPU kernel 全部本机不可用、不可验证** |
| CPU | 13th Gen i7-1355U，12 逻辑核（低压移动端） | 仅适合小模型 CPU 推理（0.5B–3B 可用，7B 很慢） |
| Ollama | **已安装 0.34.0**，服务已运行（127.0.0.1:11434），**当前无已装模型** | **GGUF 本地推理的主通道，真实可用可验证** |
| llama.cpp | 未安装（无 llama-cli/llama-server） | llama.cpp server 后端只能留扩展点，本机不可验证 |
| LM Studio | 未安装 | 同上，留扩展点 |
| Python | 3.12.11（miniforge3） | transformers/safetensors CPU 路线技术上可行，但下载大、CPU 慢，列为后续 |
| 磁盘 | C: 约 9.8GB 空闲；D: 约 20GB 空闲 | 只适合小模型（≤3B）；Ollama 默认存 C:\Users\<u>\.ollama |

**结论**：本机唯一能"真实跑起来并验证"的本地模型路线是 **Ollama（GGUF，CPU 推理）**。其余加速/后端按"扩展点 + 能力探测"实现，标注 `[环境受限]`。

---

## 1. 参考程序原子级功能对照（就本机可验证范围如实描述）

> 说明：以下为本规格设计所参照的本地模型运行时/前端。对其能力按公开常识描述；未在本机安装的部分不做臆断。

| 程序 | 角色 | 本地模型机制 | 与 AeroCode 的关系 |
|---|---|---|---|
| **Ollama** | 本地运行时 + OpenAI 兼容服务 | 管理 GGUF 模型（pull/list/show/delete/run），`/api`（原生）+ `/v1`（OpenAI 兼容） | **本机已装，是本期主集成对象** |
| **LM Studio** | 桌面 GUI + 本地服务 | 内置 llama.cpp/MLX 后端，提供 OpenAI 兼容 server | 未安装；AeroCode 已有 `LmStudioProvider`（连其 server），留扩展 |
| **llama.cpp** | 最底层 GGUF 运行时 | `llama-server` 提供 OpenAI 兼容 API；参数最全（-ngl/-c/--flash-attn/--cache-type-k 等） | 未安装；留 `LlamaCppServerProvider` 扩展点 |
| **GPT4All / Jan / KoboldCpp / LocalAI** | 桌面/服务前端 | 各自封装 GGUF 推理 | 不在本期范围 |
| **vLLM / SGLang / TGI / TensorRT-LLM** | 高吞吐 safetensors 服务 | 需 NVIDIA GPU / Docker | `[环境受限]` 本机无 NVIDIA GPU，仅能力探测 |
| **Transformers + PyTorch** | safetensors 通用推理 | CPU/GPU 均可 | 后续阶段（CPU 可行但慢） |

> 关于用户提到的 **MTP / dspark / dflash**：MTP（Multi-Token Prediction，多 token 预测投机解码）是真实技术，llama.cpp/部分框架以 speculative decoding 形式支持，将作为"加速开关"纳入参数面；**"dspark"/"dflash" 非本规格可确认的标准术语**，不作臆断实现（如实标注：待澄清具体所指，若指某类稀疏/Flash 注意力变体，归入 attention 加速开关）。

---

## 2. AeroCode 现状盘点（已有什么 / 缺什么）

### 已有
- `OpenAICompatibleProvider`（基类）+ `OllamaProvider`（连 `http://localhost:11434/v1`，免 key）+ `LmStudioProvider`。
- `ProviderFactory` 已按 `Kind` 分发：`"ollama" → OllamaProvider`、`"lmstudio" → LmStudioProvider`。
- `ProviderConfig` 含 `BaseUrl / DefaultModel / SupportsStreaming / ExtraBody / ExtraHeaders` 等。
- 设置 UI 已有 provider 编辑段。

### 缺失（本期要补）
1. **模型管理**：列出已装模型 / 拉取（pull，带进度）/ 删除 / 查看详情。—— 完全没有。
2. **Ollama 原生 API 客户端**（`/api/tags`、`/api/show`、`/api/pull`、`/api/delete`、`/api/version`、`/api/chat`）。—— 完全没有。
3. **参数调节面**：Ollama options（num_ctx / num_gpu / num_thread / temperature / top_p / top_k / repeat_penalty / 投机解码等）的原子级配置与持久化。—— 仅能靠 ExtraBody 兜底，无专用 UI。
4. **运行时检测 + 更新**：探测 Ollama 是否安装/运行/版本，检查最新版本；探测 llama.cpp/LM Studio 是否在场。—— 完全没有。
5. **本地模型设置面板**：集中管理上述能力的 UI。—— 完全没有。
6. **llama.cpp server / safetensors-transformers 后端**：扩展点。—— 完全没有。

---

## 3. 架构设计

```
AeroCode.AI/
  LocalModels/
    OllamaClient.cs            # Ollama 原生 /api + /v1 客户端（可注入 HttpMessageHandler 供测试）
    OllamaModels.cs            # DTO：OllamaModel / OllamaModelDetails / PullProgress / OllamaVersion
    ILocalModelRuntime.cs      # 运行时抽象：List/Pull/Delete/Show/Version/Health（Ollama 实现；llama.cpp 预留）
    LocalModelOptions.cs       # 参数配置 DTO（映射 Ollama options）
  Providers/
    OllamaProvider.cs          # （已有）聊天；本期让其在请求体带上 LocalModelOptions
    LlamaCppServerProvider.cs  # [扩展点] 连 llama-server 的 OpenAI 兼容端点

AeroCode.App/
  ViewModels/LocalModelsViewModel.cs   # 本地模型面板 VM（状态/列表/拉取/删除/参数）
  Views/LocalModelsView.axaml(.cs)     # 设置内"本地模型"面板
  Configuration/SettingsService.cs     # 增加 LocalModels 配置节（baseUrl/默认模型/options/自动检查更新）
```

### 数据流
- **聊天**：`ChatViewModel → ProviderFactory.Create("ollama") → OllamaProvider → POST {base}/v1/chat/completions`（携带 options）。
- **模型管理**：`LocalModelsViewModel → OllamaClient → GET/POST {base}/api/...`（base 默认 `http://127.0.0.1:11434`，注意原生 `/api` 不带 `/v1`）。
- **运行时检测**：`LocalModelsViewModel → OllamaClient.GetVersionAsync / ListModelsAsync`，不可达即如实显示"未运行/未安装"。

### 诚实语义（贯穿）
- Ollama 不可达 / 超时 / 解析失败 → 返回失败结果或 `false`，UI 显示真实原因，**绝不伪造"已连接/已加载"**。
- pull 失败 / 磁盘不足 → 如实报错。
- 本机无 NVIDIA GPU → 加速矩阵里 CUDA 系全部标 `[环境受限]`，不提供假开关。

---

## 4. 参数调节原子清单（Ollama options，映射到请求 `options`）

| 参数 | Ollama 键 | 类型/范围 | 说明 |
|---|---|---|---|
| 上下文长度 | `num_ctx` | int（2…模型上限） | 上下文窗口 |
| GPU 层数 | `num_gpu` | int（0=纯 CPU） | 本机集显收益有限，默认 0/1 |
| CPU 线程 | `num_thread` | int（默认=物理核） | CPU 推理线程 |
| 温度 | `temperature` | 0…2 | 采样温度 |
| top_p | `top_p` | 0…1 | 核采样 |
| top_k | `top_k` | int | |
| 重复惩罚 | `repeat_penalty` | float | |
| 停止词 | `stop` | string[] | |
| 种子 | `seed` | int | 可复现 |
| 最大生成 token | `num_predict` | int | 单次输出上限 |
| Mirostat | `mirostat` / `mirostat_eta` / `mirostat_tau` | | 自适应采样 |
| FlashAttention | （llama.cpp 侧）`flash_attn` | bool | `[环境受限]` 视后端/构建 |
| 投机解码/MTP | （后端侧）draft model | | `[扩展]` 视后端支持 |

> 持久化：`SettingsService.LocalModels.DefaultOptions`；单次请求可在面板覆写。映射进 Ollama `/api/chat` 的 `options` 字段，或 `/v1/chat/completions` 可透传的字段。

---

## 5. 加速技术矩阵（对照本机可得性）

| 加速技术 | 适用格式 | 本机可得性 | 处置 |
|---|---|---|---|
| CPU 推理（llama.cpp/Ollama 内核） | GGUF | ✅ 可用 | **本期主路径** |
| GPU offload（集显） | GGUF | ⚠️ 有限 | 暴露 `num_gpu`，收益如实 |
| Metal / Vulkan / SYCL | GGUF | ⚠️ Vulkan/SYCL 理论可 | 留参数，`[环境受限]` |
| CUDA / cuDNN / Tensor Core | 全部 | ❌ 无 NVIDIA GPU | `[环境受限]` 仅探测 |
| FlashAttention-2/3 | safetensors | ❌ 依赖 CUDA | `[环境受限]` |
| PagedAttention / 连续批处理 | safetensors | ❌ 需 vLLM/SGLang+GPU | `[环境受限]` |
| TensorRT-LLM / FP8 / AWQ / GPTQ / EXL2 | safetensors | ❌ 需 NVIDIA | `[环境受限]` |
| KV cache 量化 / mmap / mlock | GGUF | ⚠️ 视后端 | 留参数 |
| 投机解码 / MTP | GGUF/safetensors | ⚠️ 视后端 | `[扩展]` |
| 自动/手动更新（Ollama/运行时） | — | ✅ 可做"检查+报告" | **本期做"检查更新"，不做静默自动升级**（安全、诚实） |

---

## 6. 分阶段实施计划

| 阶段 | 内容 | 本机可验证 |
|---|---|---|
| **P1 OllamaClient** | 原生 /api（version/tags/show/pull/delete）+ /v1 chat；可注入 handler | ✅ 单测 + 真实 Ollama |
| **P2 LocalModelProvider 接线** | OllamaProvider 请求带 options；确保聊天走本地模型 | ✅ 真实 qwen2.5 |
| **P3 设置 UI + 参数持久化** | 本地模型面板（状态/列表/pull/delete/options） | ✅ UIA + 真实 |
| **P4 运行时检测 + 更新检查** | 版本探测、最新检查、llama.cpp/LM Studio 在场探测 | ✅（Ollama）/探测（其余） |
| **P5 扩展后端** | LlamaCppServerProvider、safetensors-transformers | `[环境受限]` 留扩展点 |

---

## 7. 验证计划（真实运行，拒绝"编译通过即完成"）

1. **单测**：OllamaClient 用 fake `HttpMessageHandler` 覆盖 version/tags/show/pull 进度/delete/chat 流式 + 失败路径。
2. **真实 E2E**：用已 pull 的 `qwen2.5:1.5b`，经 OllamaClient/ListModels 列出，经 LocalModelProvider 真实流式生成，断言非空且连贯输出。
3. **UIA**：本地模型面板渲染、模型列表出现 qwen2.5、状态徽标正确。
4. **回归**：全量测试套件保持绿。

---

## 8. 诚实约束（不可协商）

- 本机无 NVIDIA GPU：**不实现、不声称**任何 CUDA 系加速为"可用"；只做能力探测并标 `[环境受限]`。
- **不做静默自动升级运行时**（改驱动/装运行时风险高）；只做"检查更新 + 如实报告 + 用户手动触发"。
- Ollama 未装模型/未运行 → UI 如实显示，绝不伪造已加载。
- `dspark/dflash` 术语待澄清，不臆断实现。
