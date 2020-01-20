using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Datadog.Trace.TestHelpers
{
    public class ProfilerHelper
    {
        private static string dotNetCoreExecutable = Environment.OSVersion.Platform == PlatformID.Win32NT ? "dotnet.exe" : "dotnet";

        public static Process StartProcessWithProfiler(
            string appPath,
            bool coreClr,
            IEnumerable<string> integrationPaths,
            string profilerClsid,
            string profilerDllPath,
            string arguments = null,
            bool redirectStandardInput = false,
            int traceAgentPort = 9696)
        {
            if (appPath == null)
            {
                throw new ArgumentNullException(nameof(appPath));
            }

            if (integrationPaths == null)
            {
                throw new ArgumentNullException(nameof(integrationPaths));
            }

            if (profilerClsid == null)
            {
                throw new ArgumentNullException(nameof(profilerClsid));
            }

            // clear all relevant environment variables to start with a clean slate
            ClearProfilerEnvironmentVariables();

            ProcessStartInfo startInfo;

            if (coreClr)
            {
                // .NET Core
                startInfo = new ProcessStartInfo(dotNetCoreExecutable, $"{appPath} {arguments ?? string.Empty}");
                startInfo.EnvironmentVariables["OzCode:Agent:ForceLoad"] = "true";
                startInfo.EnvironmentVariables["CORECLR_ENABLE_PROFILING"] = "1";
                startInfo.EnvironmentVariables["CORECLR_PROFILER"] = "{3BF080FE-7DEB-4051-AEF1-BD3AB01F1883}";
                startInfo.EnvironmentVariables["CORECLR_PROFILER_PATH"] = @"C:\dev\OzCode\agent\dotnet\src\SkyApm.ProfilerMultiplexer\x64\Debug\SkyApm.ProfilerMultiplexer.dll";
                startInfo.EnvironmentVariables["CORECLR_PROFILER_HOME"] = @"C:\dev\OzCode\agent\dotnet\src\SkyApm.ClrProfiler.Trace\bin\Debug\netstandard2.0";
                startInfo.EnvironmentVariables["OzCode:Agent:ClientAddress"] = "https://ozcodeclouddebuggerclient-staging.azurewebsites.net";
                startInfo.EnvironmentVariables["OzCode:Agent:ServerAddress"] = "NoServerWriteToConsoleInstead";
                // startInfo.EnvironmentVariables["CORECLR_PROFILERS"] = "Datadog;SkyAPM";
                startInfo.EnvironmentVariables["CORECLR_PROFILERS"] = "Datadog";

                startInfo.EnvironmentVariables["CORECLR_Datadog_ENABLE_PROFILING"] = "1";
                startInfo.EnvironmentVariables["CORECLR_Datadog_PROFILER"] = profilerClsid;
                startInfo.EnvironmentVariables["CORECLR_Datadog_PROFILER_PATH"] = profilerDllPath;
                startInfo.EnvironmentVariables["CORECLR_Datadog_PRIORITY"] = "1.0";

                startInfo.EnvironmentVariables["CORECLR_SkyAPM_ENABLE_PROFILING"] = "1";
                startInfo.EnvironmentVariables["CORECLR_SkyAPM_PROFILER"] = "{09992526-3e32-4995-8d3d-a97c839393e1}";
                startInfo.EnvironmentVariables["CORECLR_SkyAPM_PROFILER_PATH"] = @"C:\dev\OzCode\agent\dotnet\src\SkyApm.ClrProfiler\x64\Debug\SkyApm.ClrProfiler.dll";
                startInfo.EnvironmentVariables["CORECLR_SkyAPM_PRIORITY"] = "2.0";

                startInfo.EnvironmentVariables["DD_PROFILER_PROCESSES"] = dotNetCoreExecutable;
            }
            else
            {
                // .NET Framework
                startInfo = new ProcessStartInfo(appPath, $"{arguments ?? string.Empty}");

                startInfo.EnvironmentVariables["OzCode:Agent:ForceLoad"] = "true";
                startInfo.EnvironmentVariables["COR_ENABLE_PROFILING"] = "1";
                startInfo.EnvironmentVariables["COR_PROFILER"] = "{DBE6D54B-4A04-4FD0-83EB-12A1A9DEC58B}";
                startInfo.EnvironmentVariables["COR_PROFILER_PATH"] = @"C:\dev\OzCode\agent\dotnet\src\SkyApm.ProfilerMultiplexer\x64\Debug\SkyApm.ProfilerMultiplexer.dll";
                startInfo.EnvironmentVariables["COR_PROFILER_HOME"] = @"C:\dev\OzCode\agent\dotnet\src\SkyApm.ClrProfiler.Trace\bin\Debug\net461";
                startInfo.EnvironmentVariables["OzCode:Agent:ClientAddress"] = "https://ozcodeclouddebuggerclient-staging.azurewebsites.net";
                startInfo.EnvironmentVariables["OzCode:Agent:ServerAddress"] = "NoServerWriteToConsoleInstead";
                startInfo.EnvironmentVariables["COR_PROFILERS"] = "Datadog;SkyAPM";
                // startInfo.EnvironmentVariables["COR_PROFILERS"] = "Datadog";

                startInfo.EnvironmentVariables["COR_Datadog_ENABLE_PROFILING"] = "1";
                startInfo.EnvironmentVariables["COR_Datadog_PROFILER"] = profilerClsid;
                startInfo.EnvironmentVariables["COR_Datadog_PROFILER_PATH"] = profilerDllPath;
                startInfo.EnvironmentVariables["COR_Datadog_PRIORITY"] = "2.0";

                startInfo.EnvironmentVariables["COR_SkyAPM_ENABLE_PROFILING"] = "1";
                startInfo.EnvironmentVariables["COR_SkyAPM_PROFILER"] = "{af0d821e-299b-5307-a3d8-b283c03916dd}";
                startInfo.EnvironmentVariables["COR_SkyAPM_PROFILER_PATH"] = @"C:\dev\OzCode\agent\dotnet\src\SkyApm.ClrProfiler\x64\Debug\SkyApm.ClrProfiler.dll";
                startInfo.EnvironmentVariables["COR_SkyAPM_PRIORITY"] = "1.0";

                string executableFileName = Path.GetFileName(appPath);
                startInfo.EnvironmentVariables["DD_PROFILER_PROCESSES"] = executableFileName;
            }

            string integrations = string.Join(";", integrationPaths);
            startInfo.EnvironmentVariables["DD_INTEGRATIONS"] = integrations;
            startInfo.EnvironmentVariables["DD_TRACE_AGENT_HOSTNAME"] = "localhost";
            startInfo.EnvironmentVariables["DD_TRACE_AGENT_PORT"] = traceAgentPort.ToString();

            // for ASP.NET Core sample apps, set the server's port
            startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = $"http://localhost:{traceAgentPort}/";

            foreach (var name in new string[] { "REDIS_HOST" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    startInfo.EnvironmentVariables[name] = value;
                }
            }

            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = redirectStandardInput;

            return Process.Start(startInfo);
        }

        public static void ClearProfilerEnvironmentVariables()
        {
            var environmentVariables = new[]
                                       {
                                           // .NET Core
                                           "CORECLR_ENABLE_PROFILING",
                                           "CORECLR_PROFILER",
                                           "CORECLR_PROFILER_PATH",
                                           "CORECLR_PROFILER_PATH_32",
                                           "CORECLR_PROFILER_PATH_64",

                                           // .NET Framework
                                           "COR_ENABLE_PROFILING",
                                           "COR_PROFILER",
                                           "COR_PROFILER_PATH",

                                           // Datadog
                                           "DD_PROFILER_PROCESSES",
                                           "DD_INTEGRATIONS",
                                           "DATADOG_PROFILER_PROCESSES",
                                           "DATADOG_INTEGRATIONS",
                                       };

            foreach (string variable in environmentVariables)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }
    }
}
