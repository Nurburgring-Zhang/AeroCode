// Copyright (c) AeroCode
// WindowsJobSandbox — run_shell 的 Job Object 隔离（批次 B G4，builder-γ）。
// Win32 Job Object P/Invoke：内存上限 / 活跃进程上限 / KillOnJobClose；零 mock，行为全部可真实验证。
//
// >>> [降维实现] 显式标注 <<<
// 本组件只覆盖 Job Object 级隔离（内存/进程数/同生共死），【未含】restricted token
// （AppContainer / 受限令牌）级文件系统与网络隔离——那是批次 C 议题（见批次B计划表 风险预案）。
// 在受限令牌落地前，本沙箱不能宣称"逃逸不可能"，只能保证"超限即杀/随沙箱消亡"。
// 文件系统写权限仍由 WorkspaceBoundary + 守卫链（ToolGuardChain）承担，勿混淆两层边界。
//
// ShellRunner 可选挂接（接线点，不改动 ShellRunner 归属文件）：
//   using var job = new WindowsJobSandbox(processMemoryLimitBytes: 1L << 30, maxActiveProcesses: 32);
//   using var p = job.Start(psi);   // Start + Assign 原子化（Assign 失败即杀刚拉起的进程并抛出）
//   ...等待/读取输出同 ShellRunner 现有路径...
// 不挂接时行为不变（null 沙箱直通），满足"无 Job 行为不变"验收。
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AeroAgent.Moa.Tools.Workspace;

/// <summary>
/// 一个 Win32 Job Object 封装：把子进程圈进受限资源域。
/// - processMemoryLimitBytes：单进程提交内存上限（超限分配失败，进程自身崩溃退出，真实可测）；
/// - jobMemoryLimitBytes：全 job 提交内存上限；
/// - maxActiveProcesses：job 内活跃进程数上限（超限 CreateProcess 失败，孙进程起不来，真实可测）；
/// - perProcessCpuTimeLimit：单进程用户态 CPU 时间上限——超限内核直接终止该进程
///   （强制执行不依赖系统提交限制，是"超限即杀"在任何机器上都可验证的通道）；
/// - killOnJobClose（默认 true）：沙箱 Dispose/句柄关闭 → 整棵进程树被杀（不留孤儿）；
///   显式传 false 可创建"只限资源、不随沙箱消亡"的软沙箱。
/// 【执行环境注意】JOB_OBJECT_LIMIT_PROCESS_MEMORY / JOB_OBJECT_LIMIT_JOB_MEMORY 的强制执行
/// 依赖系统提交限制被强制（页面文件固定大小/禁用自动增长；见 Windows Internals：提交限额
/// 可动态扩张时不强制 per-job commit limits）。本组件照常设置内核限额（内核可查回），测试侧
/// 对"内存超限被杀"按环境能力门控，不伪造强制行为。
/// </summary>
public sealed class WindowsJobSandbox : IDisposable
{
    // JOB_OBJECT_LIMIT_*（winbase.h）
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const uint JOB_OBJECT_LIMIT_PROCESS_TIME = 0x00000002;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    private IntPtr _handle;
    private readonly bool _killOnJobClose;
    private bool _disposed;

    public WindowsJobSandbox(
        long? processMemoryLimitBytes = null,
        long? jobMemoryLimitBytes = null,
        int? maxActiveProcesses = null,
        TimeSpan? perProcessCpuTimeLimit = null,
        bool killOnJobClose = true)
    {
        if (processMemoryLimitBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(processMemoryLimitBytes));
        if (jobMemoryLimitBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(jobMemoryLimitBytes));
        if (maxActiveProcesses is <= 0) throw new ArgumentOutOfRangeException(nameof(maxActiveProcesses));
        if (perProcessCpuTimeLimit.HasValue && perProcessCpuTimeLimit.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(perProcessCpuTimeLimit));

        _killOnJobClose = killOnJobClose;

        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateJobObject failed (win32 error {Marshal.GetLastWin32Error()})");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        var flags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; // 沙箱消亡 = 进程树消亡（默认语义，显式可关）
        if (!killOnJobClose)
        {
            flags &= ~JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        }

        if (processMemoryLimitBytes is { } pm)
        {
            flags |= JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            info.ProcessMemoryLimit = new UIntPtr((ulong)pm);
        }

        if (jobMemoryLimitBytes is { } jm)
        {
            flags |= JOB_OBJECT_LIMIT_JOB_MEMORY;
            info.JobMemoryLimit = new UIntPtr((ulong)jm);
        }

        if (maxActiveProcesses is { } ap)
        {
            flags |= JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
            info.BasicLimitInformation.ActiveProcessLimit = (uint)ap;
        }

        if (perProcessCpuTimeLimit is { } cpu)
        {
            flags |= JOB_OBJECT_LIMIT_PROCESS_TIME;
            // PerProcessUserTimeLimit 单位 = 100ns，与 TimeSpan.Ticks 一致
            info.BasicLimitInformation.PerProcessUserTimeLimit = cpu.Ticks;
        }

        info.BasicLimitInformation.LimitFlags = flags;

        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var err = Marshal.GetLastWin32Error();
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException($"SetInformationJobObject failed (win32 error {err})");
        }
    }

    /// <summary>原生 job 句柄（测试/诊断可断言非零）。</summary>
    public IntPtr Handle => _handle;

    /// <summary>把已启动进程圈入本 job。失败如实抛（进程是否已被圈走未知时调用方自行决定处置）。</summary>
    public void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new InvalidOperationException(
                $"AssignProcessToJobObject failed for pid {process.Id} (win32 error {Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>
    /// 拉起进程并立即圈入本 job（Start+Assign 紧凑执行，收窄逃逸窗口）。
    /// 拉起成功但圈入失败 → 先杀掉刚拉起的进程再抛（绝不留一个"以为被隔离其实在裸奔"的进程）。
    /// </summary>
    public Process Start(ProcessStartInfo psi)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        try
        {
            Assign(process);
            return process;
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            throw;
        }
    }

    /// <summary>立即终止 job 内全部进程（幂等；已退出/空 job 不报错）。</summary>
    public void Terminate(uint exitCode = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TerminateJobObject(_handle, exitCode);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            // killOnJobClose=true：显式 Terminate 让"沙箱消亡即全灭"在任何情况下确定成立（幂等）；
            // killOnJobClose=false（软沙箱）：只关句柄放行资源，进程继续运行。
            if (_killOnJobClose)
            {
                try { TerminateJobObject(_handle, 1); } catch { /* 句柄已失效/进程已退 */ }
            }

            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // ---- Win32 P/Invoke ----

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
