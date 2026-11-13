using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Infrastructure.Tools.SystemInfo;

/// <summary>Category "system": read-only facts about the machine (auto-approved).</summary>
internal sealed class SystemInfoToolProvider : IAgentToolProvider
{
    public string Category => "system";

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new AgentToolDescriptor(
            AIFunctionFactory.Create(GetSystemInfo, "get_system_info", "Operating system, CPU architecture, .NET runtime, machine and user name, current directory, local time and uptime."),
            ToolRisk.ReadOnly,
            Category);
    }

    private static object GetSystemInfo() => new
    {
        os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.OSArchitecture.ToString(),
        runtime = RuntimeInformation.FrameworkDescription,
        machine = Environment.MachineName,
        user = Environment.UserName,
        processors = Environment.ProcessorCount,
        currentDirectory = Environment.CurrentDirectory,
        localTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture),
        timeZone = TimeZoneInfo.Local.DisplayName,
        uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\.hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture),
        defaultShell = OperatingSystem.IsWindows() ? "powershell" : "bash",
    };
}
