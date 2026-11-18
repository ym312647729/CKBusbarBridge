using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace RouteLinkBar.Algorithm.Worker
{
    internal static class Program
    {
        private static int Main(string[] commandLineArguments)
        {
            try
            {
                RaiseThreadPoolMinimumWorkerThreads();
                var parentProcessId = ReadParentProcessId(commandLineArguments);
                using (var standardInputReader = new StreamReader(
                    Console.OpenStandardInput(),
                    new UTF8Encoding(false),
                    false,
                    4096,
                    false))
                using (var protocolOutputWriter = new StreamWriter(
                    Console.OpenStandardOutput(),
                    new UTF8Encoding(false),
                    4096,
                    false))
                using (var parentProcessMonitor = CreateParentProcessMonitor(parentProcessId))
                {
                    protocolOutputWriter.AutoFlush = true;
                    // 防止算法中的临时 Console.WriteLine 污染 stdout 单行 Json 协议。
                    Console.SetOut(TextWriter.Null);
                    var commandLoop = new LinkageRouteWorkerCommandLoop(
                        new CompatibilityInputReader(standardInputReader),
                        protocolOutputWriter,
                        Console.Error,
                        parentProcessId);
                    return commandLoop.Run();
                }
            }
            catch (Exception fatalException)
            {
                Console.Error.WriteLine("算法 Worker 启动失败：" + fatalException);
                return 1;
            }
        }

        // 搜索的 Parallel 分支会在线程池线程上等待主搜索/优化名额；.NET Framework
        // 默认最小线程数等于核数，超出后每秒仅缓慢补充线程，阻塞等待易造成饥饿。
        // 只提高立即可用的下限，最大线程数和算法并行度配置保持不变。
        private static void RaiseThreadPoolMinimumWorkerThreads()
        {
            ThreadPool.GetMinThreads(
                out var minimumWorkerThreads,
                out var minimumCompletionPortThreads);
            var requiredMinimumWorkerThreads = Math.Max(
                minimumWorkerThreads,
                System.Environment.ProcessorCount * 2);
            if (requiredMinimumWorkerThreads > minimumWorkerThreads)
            {
                ThreadPool.SetMinThreads(
                    requiredMinimumWorkerThreads,
                    minimumCompletionPortThreads);
            }
        }

        private static int ReadParentProcessId(string[] commandLineArguments)
        {
            for (var argumentIndex = 0;
                argumentIndex + 1 < (commandLineArguments?.Length ?? 0);
                argumentIndex++)
            {
                if (string.Equals(
                        commandLineArguments[argumentIndex],
                        "--parent-pid",
                        StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(
                        commandLineArguments[argumentIndex + 1],
                        out var parsedParentProcessId) &&
                    parsedParentProcessId > 0)
                {
                    return parsedParentProcessId;
                }
            }

            throw new ArgumentException("缺少有效的 --parent-pid 参数。" );
        }

        private static Timer CreateParentProcessMonitor(int parentProcessId)
        {
            return new Timer(
                ignoredState =>
                {
                    try
                    {
                        using (var parentProcess = Process.GetProcessById(parentProcessId))
                        {
                            if (!parentProcess.HasExited)
                            {
                                return;
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        // 父进程已经退出。
                    }
                    catch (InvalidOperationException)
                    {
                        // 父进程句柄已经失效。
                    }

                    System.Environment.Exit(0);
                },
                null,
                TimeSpan.FromSeconds(1d),
                TimeSpan.FromSeconds(1d));
        }
    }
}
