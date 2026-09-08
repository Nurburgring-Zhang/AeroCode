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
// R4 α（S-MED-4 修复）：Start 的 Process.Start→Assign 序列存在竞态窗口——根进程在 Start 返回
// 与 Assign 之间派生的孙进程不在 job 内（不受限额、不随 KillOnJobClose 全灭）。修复 = 标准做法：
// CreateProcessW 以 CREATE_SUSPENDED 创建挂起进程 → AssignProcessToJobObject 圈入 → ResumeThread
// 恢复主线程——进程执行第一条用户态指令前必已在 job 内，逃逸窗口归零。
// 输出重定向由 StartSuspended 自管匿名管道（STARTUPINFO 挂 hStdOutput/hStdError），调用方经
// SandboxedStartResult 拿 Process + StreamReader。旧 Start（Process.Start 路径）保留兼容
// 既有测试与无重定向场景；生产 enforce 路径（ShellRunner）已切换到 StartSuspended。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

    // CreateProcessW 创建标志 / STARTUPINFO 标志（S-MED-4 挂起圈入路径）
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x0100;
    private const int STD_INPUT_HANDLE = -10;

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
    /// 注意：本路径存在 Start 与 Assign 之间的竞态窗口（根进程抢先派生的孙进程不在 job 内）；
    /// 生产 enforce 路径请用 <see cref="StartSuspended"/>（CREATE_SUSPENDED，窗口归零）。
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

    /// <summary>
    /// S-MED-4 修复路径：CREATE_SUSPENDED 创建挂起进程 → 圈入 job → ResumeThread 恢复主线程。
    /// 进程执行第一条用户态指令前必已在 job 内——Start→Assign 竞态窗口归零，
    /// 根进程抢先派生的孙进程也必然受 job 限额约束并随 KillOnJobClose 全灭。
    /// 输出重定向由本方法自管匿名管道（stdout/stderr 各一条），调用方经
    /// <see cref="SandboxedStartResult"/> 拿 Process + StreamReader 自行泵读。
    /// 支持 ProcessStartInfo 子集：FileName/Arguments/WorkingDirectory/CreateNoWindow/
    /// RedirectStandardOutput+Error（必须为 true）；stdin 重定向不支持（直抛）；
    /// 环境变量一律继承父进程（psi 上的改写被忽略，见方法体内注释）。任何一步失败 → 已创建的挂起进程先终止再抛。
    /// </summary>
    public SandboxedStartResult StartSuspended(ProcessStartInfo psi, Encoding? outputEncoding = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(psi);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("StartSuspended relies on Win32 CreateProcess/Job Object (Windows-only).");
        }
        if (psi.UseShellExecute)
        {
            throw new NotSupportedException("StartSuspended requires UseShellExecute=false.");
        }
        if (!psi.RedirectStandardOutput || !psi.RedirectStandardError)
        {
            throw new NotSupportedException("StartSuspended requires RedirectStandardOutput and RedirectStandardError (self-managed pipes).");
        }
        if (psi.RedirectStandardInput)
        {
            throw new NotSupportedException("StartSuspended does not support stdin redirection.");
        }

        // 环境语义：一律继承父进程环境（lpEnvironment=null）。psi.Environment/EnvironmentVariables
        // 的 getter 会把父进程全量环境拷入字典（访问副作用），无法与显式改写区分，故不做检查；
        // psi 上的环境改写在本路径被忽略——生产唯一调用方 ShellRunner 从不设置环境变量。

        var encoding = outputEncoding ?? Console.OutputEncoding;
        IntPtr stdoutRead = IntPtr.Zero, stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero, stderrWrite = IntPtr.Zero;
        Process? process = null;
        try
        {
            CreateInheritablePipe(out stdoutRead, out stdoutWrite);
            CreateInheritablePipe(out stderrRead, out stderrWrite);

            var commandLine = new StringBuilder(QuoteArgument(psi.FileName));
            if (!string.IsNullOrEmpty(psi.Arguments))
            {
                commandLine.Append(' ').Append(psi.Arguments);
            }

            var si = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                dwFlags = STARTF_USESTDHANDLES,
                hStdInput = GetStdHandle(STD_INPUT_HANDLE),
                hStdOutput = stdoutWrite,
                hStdError = stderrWrite,
            };

            var creationFlags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT;
            if (psi.CreateNoWindow)
            {
                creationFlags |= CREATE_NO_WINDOW;
            }

            if (!CreateProcessW(
                    null, commandLine, IntPtr.Zero, IntPtr.Zero,
                    bInheritHandles: true, creationFlags, IntPtr.Zero,
                    string.IsNullOrEmpty(psi.WorkingDirectory) ? null : psi.WorkingDirectory,
                    ref si, out var pi))
            {
                throw new InvalidOperationException($"CreateProcessW failed (win32 error {Marshal.GetLastWin32Error()})");
            }

            // 挂起态圈入：进程尚未执行任何用户态指令，圈入成功后才恢复主线程。
            if (!AssignProcessToJobObject(_handle, pi.hProcess))
            {
                var err = Marshal.GetLastWin32Error();
                try { TerminateProcess(pi.hProcess, 1); } catch { /* 尽力而为 */ }
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                throw new InvalidOperationException($"AssignProcessToJobObject failed for suspended pid {pi.dwProcessId} (win32 error {err})");
            }

            if (ResumeThread(pi.hThread) == unchecked((uint)-1))
            {
                var err = Marshal.GetLastWin32Error();
                try { TerminateProcess(pi.hProcess, 1); } catch { /* 尽力而为 */ }
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                throw new InvalidOperationException($"ResumeThread failed for suspended pid {pi.dwProcessId} (win32 error {err})");
            }

            // 圈入+恢复成功：包装托管 Process（按 pid 另开句柄），随后释放 CreateProcess 的原生句柄
            // 与子进程侧管道写端（父进程关掉写端，子进程退出后读端才能收到 EOF）。
            process = Process.GetProcessById(pi.dwProcessId);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            CloseHandle(stdoutWrite); stdoutWrite = IntPtr.Zero;
            CloseHandle(stderrWrite); stderrWrite = IntPtr.Zero;

            var stdoutReader = new StreamReader(
                new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(stdoutRead, ownsHandle: true), FileAccess.Read, bufferSize: 4096, isAsync: false),
                encoding);
            var stderrReader = new StreamReader(
                new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(stderrRead, ownsHandle: true), FileAccess.Read, bufferSize: 4096, isAsync: false),
                encoding);
            stdoutRead = IntPtr.Zero;
            stderrRead = IntPtr.Zero;
            return new SandboxedStartResult(process, stdoutReader, stderrReader);
        }
        catch
        {
            // fail-closed 清理：任何一步失败 → 挂起进程终止、管道句柄全关、不留泄漏也不留裸奔进程。
            try { process?.Kill(entireProcessTree: true); } catch { /* 可能尚未恢复/已终止 */ }
            process?.Dispose();
            foreach (var h in new[] { stdoutRead, stdoutWrite, stderrRead, stderrWrite })
            {
                if (h != IntPtr.Zero)
                {
                    CloseHandle(h);
                }
            }

            throw;
        }
    }

    /// <summary>创建匿名管道（子进程端可继承）：父读子写。</summary>
    private static void CreateInheritablePipe(out IntPtr readHandle, out IntPtr writeHandle)
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = true,
        };
        if (!CreatePipe(out readHandle, out writeHandle, ref sa, 0))
        {
            throw new InvalidOperationException($"CreatePipe failed (win32 error {Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>命令行参数引号处理（含空格且未带引号 → 包引号；其余原样）。</summary>
    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        if (arg.Contains(' ') && !arg.StartsWith('"'))
        {
            return $"\"{arg}\"";
        }

        return arg;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

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

/// <summary>
/// <see cref="WindowsJobSandbox.StartSuspended"/> 的一次启动结果：已圈入 job 并已恢复的进程
/// + 自管输出管道读端（调用方负责泵读与 Dispose；Process 与两个 Reader 均需释放）。
/// </summary>
public sealed record SandboxedStartResult(
    Process Process,
    StreamReader StandardOutput,
    StreamReader StandardError) : IDisposable
{
    public void Dispose()
    {
        try { Process.Dispose(); } catch { /* 进程可能已终止 */ }
        try { StandardOutput.Dispose(); } catch { /* 管道可能已关闭 */ }
        try { StandardError.Dispose(); } catch { /* 管道可能已关闭 */ }
    }
}
