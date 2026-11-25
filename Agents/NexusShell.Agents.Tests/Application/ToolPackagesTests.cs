using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NexusShell.Agents.Application.Abstractions.Skills;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Application.ToolPackages;
using NexusShell.Agents.Domain.ToolPackages;
using NexusShell.Agents.Domain.Tools;
using NexusShell.Agents.Infrastructure.ToolPackages;
using NexusShell.Agents.Tests.Fakes;

namespace NexusShell.Agents.Tests.Application;

/// <summary>
/// NexusShell tools: folders with tool.json + SKILL.md + scripts that the agent uses like any tool,
/// with checked arguments, progress, results and a one-time setup that asks the user first.
/// </summary>
public sealed class ToolPackagesTests : IDisposable
{
    private readonly AgentTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private string ToolsRoot => Path.Combine(_host.Root, "tool-data");

    private string OutputRoot => Path.Combine(_host.Root, "results");

    private ServiceProvider Build() => _host.Build(s =>
        s.AddSingleton(new ToolPackageOptions { Root = ToolsRoot, OutputRoot = OutputRoot }));

    private AgentTestHost Experts() => _host.Expert("alpha", "Alpha", "apples", "tools: [test]").Expert("beta", "Beta", "bananas");

    private const string EchoManifest = """
        {
          "name": "echo-file",
          "title": "Echo a file",
          "description": "Reads a text file and says it back louder.",
          "runtime": "powershell",
          "entry": "run.ps1",
          "risk": "read",
          "timeoutMinutes": 2,
          "parameters": [
            { "name": "file", "type": "file", "required": true, "description": "The text file.", "extensions": [".txt"] },
            { "name": "shout", "type": "boolean", "default": false, "description": "Upper case." },
            { "name": "style", "type": "choice", "choices": ["plain", "fancy"], "default": "plain" }
          ]
        }
        """;

    private const string EchoScript = """
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $a = Get-Content -Raw -Encoding UTF8 $env:NEXUS_TOOL_ARGS | ConvertFrom-Json
        Write-Output 'a library printing something that is not an event'
        @{ event = 'progress'; message = 'Reading'; percent = 50 } | ConvertTo-Json -Compress
        New-Item -ItemType Directory -Force $env:NEXUS_TOOL_OUTPUT | Out-Null
        $out = Join-Path $env:NEXUS_TOOL_OUTPUT 'echo.txt'
        $text = (Get-Content -Raw -Encoding UTF8 $a.file).Trim()
        if ($a.shout) { $text = $text.ToUpper() }
        Set-Content -Encoding UTF8 -Path $out -Value $text
        @{ event = 'result'; text = "Echo ($($a.style)): $text"; files = @($out) } | ConvertTo-Json -Compress
        """;

    private string EchoTool(string script = EchoScript, string manifest = EchoManifest, string name = "echo-file")
    {
        _host.WriteBundled(Path.Combine("tools", name, "tool.json"), manifest);
        _host.WriteBundled(Path.Combine("tools", name, "run.ps1"), script);
        _host.WriteBundled(Path.Combine("tools", name, "SKILL.md"), $"---\nname: {name}\ndescription: Echo a text file.\n---\nCall tool_echo_file with the file.");
        return Path.Combine(_host.Bundled, "tools", name);
    }

    private string TextFile(string text = "hello apples")
    {
        string path = Path.Combine(_host.Root, "notes.txt");
        File.WriteAllText(path, text);
        return path;
    }

    // ---------- The format ----------

    [Fact]
    public void The_shipped_transcribe_tool_is_a_valid_package()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Agent", "tools", "transcribe");

        var package = FileToolPackageCatalog.Read(folder, bundled: true);

        Assert.Equal("tool_transcribe", package.FunctionName);
        Assert.Equal(ToolRuntimeKind.Python, package.Runtime);
        Assert.Equal(ToolRisk.ReadOnly, package.Risk);
        Assert.Equal(2, package.InstallSteps.Count);
        Assert.Equal("https://download.pytorch.org/whl/cpu", package.InstallSteps[0].IndexUrl);
        Assert.Contains(".mp4", package.FileExtensions);
        Assert.True(package.Parameters.Single(p => p.Name == "file").Required);
        Assert.Equal(["turbo", "small", "medium", "large-v3"], package.Parameters.Single(p => p.Name == "model").Choices);
        Assert.NotNull(package.SkillFile);
        Assert.Contains("GB", package.SetupSize, StringComparison.Ordinal);
        // Sada's engine is there, without its window, microphone or service.
        Assert.True(File.Exists(Path.Combine(folder, "engine", "job.py")));
        Assert.False(File.Exists(Path.Combine(folder, "engine", "mic.py")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Agent", "tools", "_sdk", "nexus_tool.py")));
    }

    [Theory]
    [InlineData("\"name\": \"echo-file\"", "\"name\": \"Echo File!\"", "name")]
    [InlineData("\"entry\": \"run.ps1\"", "\"entry\": \"../run.ps1\"", "inside the tool folder")]
    [InlineData("\"entry\": \"run.ps1\"", "\"entry\": \"missing.ps1\"", "does not exist")]
    [InlineData("\"runtime\": \"powershell\"", "\"runtime\": \"ruby\"", "not supported")]
    [InlineData("\"type\": \"choice\", \"choices\": [\"plain\", \"fancy\"]", "\"type\": \"choice\"", "lists no choices")]
    [InlineData("\"risk\": \"read\"", "\"risk\": \"maybe\"", "risk")]
    [InlineData("\"timeoutMinutes\": 2", "\"timeoutMinutes\": 0", "timeoutMinutes")]
    public void A_wrong_manifest_is_reported_and_skipped(string replace, string with, string reason)
    {
        EchoTool(manifest: EchoManifest.Replace(replace, with, StringComparison.Ordinal));
        using var services = Build();
        var catalog = services.GetRequiredService<IToolPackageCatalog>();

        Assert.Empty(catalog.List());
        Assert.Contains(reason, Assert.Single(catalog.Problems), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_steps_must_use_https_and_real_package_names()
    {
        string python = EchoManifest.Replace("\"runtime\": \"powershell\"", "\"runtime\": \"python\"").Replace("\"entry\": \"run.ps1\"", "\"entry\": \"run.py\"");
        _host.WriteBundled(Path.Combine("tools", "echo-file", "run.py"), "print('x')");
        EchoTool(manifest: python.Replace("\"risk\"", "\"install\": [ { \"packages\": [\"numpy>=2.0\"], \"indexUrl\": \"http://example.test/simple\" } ], \"risk\""));
        using var services = Build();
        Assert.Contains("https", Assert.Single(services.GetRequiredService<IToolPackageCatalog>().Problems), StringComparison.Ordinal);

        EchoTool(manifest: python.Replace("\"risk\"", "\"install\": [ { \"packages\": [\"numpy; rm -rf /\"] } ], \"risk\""));
        services.GetRequiredService<IToolPackageCatalog>().Reload();
        Assert.Contains("not a package name", Assert.Single(services.GetRequiredService<IToolPackageCatalog>().Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_user_tool_replaces_the_shipped_one_with_the_same_name()
    {
        EchoTool();
        _host.WriteUser(Path.Combine("tools", "echo-file", "tool.json"), EchoManifest.Replace("Echo a file", "My own echo", StringComparison.Ordinal));
        _host.WriteUser(Path.Combine("tools", "echo-file", "run.ps1"), EchoScript);
        using var services = Build();

        var package = Assert.Single(services.GetRequiredService<IToolPackageCatalog>().List());
        Assert.Equal("My own echo", package.Title);
        Assert.False(package.Bundled);
    }

    [Fact]
    public void The_agent_sees_each_tool_as_a_function_with_its_parameters()
    {
        EchoTool();
        using var services = Build();
        var tools = services.GetRequiredService<ToolPackageToolProvider>().GetTools().ToList();

        var echo = tools.Single(t => t.Name == "tool_echo_file");
        Assert.Equal(ToolRisk.ReadOnly, echo.Risk);
        var schema = echo.Function.JsonSchema;
        Assert.Equal("file", schema.GetProperty("required")[0].GetString());
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("shout").GetProperty("type").GetString());
        Assert.Equal(["plain", "fancy"], schema.GetProperty("properties").GetProperty("style").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(".txt", schema.GetProperty("properties").GetProperty("file").GetProperty("description").GetString(), StringComparison.Ordinal);

        var setup = tools.Single(t => t.Name == ToolPackageToolProvider.SetupFunctionName);
        Assert.True(setup.ForceApproval); // downloads always ask
    }

    [Fact]
    public void The_tools_skill_is_readable_by_the_agent()
    {
        EchoTool();
        using var services = Build();

        Assert.Contains(services.GetRequiredService<ISkillsProviderFactory>().ListSkills(), s => s.Name == "echo-file");
    }

    // ---------- Running ----------

    [Fact]
    public async Task The_agent_runs_a_tool_on_a_file_and_gets_its_result()
    {
        Experts();
        EchoTool();
        string file = TextFile();
        int call = 0;
        _host.Script((messages, _) => Interlocked.Increment(ref call) == 1
            ? ScriptedChatClient.Call("c1", "tool_echo_file", new Dictionary<string, object?> { ["file"] = file, ["shout"] = true })
            : ScriptedChatClient.Text("Done: " + ScriptedChatClient.LastToolResult(messages)?.Result));
        await using var services = Build();
        var steps = new ConcurrentQueue<AgentProgress>();

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var reply = await conversation.SendAsync("apples: use the echo tool on my notes", new SynchronousProgress(steps.Enqueue));

        Assert.Contains("Echo (plain): HELLO APPLES", reply.Text, StringComparison.Ordinal);
        Assert.Contains(ToolPackageFunction.OutputNote, reply.Text, StringComparison.Ordinal); // tool output is marked as data
        string output = Path.Combine(OutputRoot, "echo-file", "echo.txt");
        Assert.Contains(output, reply.Text, StringComparison.Ordinal);
        Assert.Equal("HELLO APPLES", File.ReadAllText(output).Trim().TrimStart('\uFEFF'));
        Assert.Contains(steps, s => s.Kind == AgentProgressKind.ToolProgress && s.Message == "Reading (50%)" && s.ToolName == "Echo a file");
        Assert.Empty(_host.Approvals.Requests); // a "read" tool does not ask
    }

    [Theory]
    [InlineData(null, "\"file\" is required")]
    [InlineData("missing.txt", "does not exist")]
    [InlineData("notes.md", "not a supported file")]
    public async Task Wrong_arguments_are_refused_before_anything_runs(string? fileName, string reason)
    {
        EchoTool();
        string folder = _host.Root;
        File.WriteAllText(Path.Combine(folder, "notes.md"), "x");
        await using var services = Build();
        var function = services.GetRequiredService<ToolPackageToolProvider>().GetTools().Single(t => t.Name == "tool_echo_file").Function;

        var arguments = new AIFunctionArguments();
        if (fileName is not null) arguments["file"] = Path.Combine(folder, fileName);
        string result = (await function.InvokeAsync(arguments))?.ToString() ?? string.Empty;

        Assert.StartsWith("Not run:", result, StringComparison.Ordinal);
        Assert.Contains(reason, result, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(OutputRoot, "echo-file")));
    }

    [Fact]
    public void Arguments_are_converted_to_their_kinds()
    {
        string folder = EchoTool();
        var package = FileToolPackageCatalog.Read(folder, bundled: true);
        string file = TextFile();

        var values = ToolArguments.Check(package, new Dictionary<string, object?>
        {
            ["file"] = JsonSerializer.SerializeToElement("notes.txt"),
            ["shout"] = JsonSerializer.SerializeToElement("yes"),
            ["style"] = "FANCY",
        }, workingDirectory: _host.Root);

        Assert.Equal(file, values["file"]);
        Assert.Equal(true, values["shout"]);
        Assert.Equal("fancy", values["style"]);
        Assert.Throws<ArgumentException>(() => ToolArguments.Check(package, new Dictionary<string, object?> { ["file"] = file, ["style"] = "loud" }, null));
        Assert.Throws<ArgumentException>(() => ToolArguments.Check(package, new Dictionary<string, object?> { ["file"] = file, ["color"] = "red" }, null));
    }

    [Fact]
    public async Task A_failing_script_explains_why()
    {
        EchoTool(script: """
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            @{ event = 'error'; message = 'The file is locked by another program.' } | ConvertTo-Json -Compress
            exit 3
            """);
        await using var services = Build();
        var function = services.GetRequiredService<ToolPackageToolProvider>().GetTools().Single(t => t.Name == "tool_echo_file").Function;

        string result = (await function.InvokeAsync(new AIFunctionArguments { ["file"] = TextFile() }))?.ToString() ?? string.Empty;

        Assert.Equal("Echo a file failed: The file is locked by another program.", result);
    }

    [Fact]
    public async Task A_crashing_script_reports_its_last_error_lines()
    {
        EchoTool(script: "[Console]::Error.WriteLine('Traceback: something broke'); exit 1");
        await using var services = Build();
        var function = services.GetRequiredService<ToolPackageToolProvider>().GetTools().Single(t => t.Name == "tool_echo_file").Function;

        string result = (await function.InvokeAsync(new AIFunctionArguments { ["file"] = TextFile() }))?.ToString() ?? string.Empty;

        Assert.Contains("failed", result, StringComparison.Ordinal);
        Assert.Contains("something broke", result, StringComparison.Ordinal);
    }

    // ---------- Setup ----------

    [Fact]
    public async Task A_python_tool_that_is_not_set_up_says_what_the_setup_downloads()
    {
        string manifest = EchoManifest
            .Replace("\"runtime\": \"powershell\"", "\"runtime\": \"python\", \"setupSize\": \"about 2 GB, once\"")
            .Replace("\"entry\": \"run.ps1\"", "\"entry\": \"run.py\"");
        _host.WriteBundled(Path.Combine("tools", "echo-file", "run.py"), "print('x')");
        EchoTool(manifest: manifest);
        await using var services = Build();
        var tools = services.GetRequiredService<ToolPackageToolProvider>();
        var info = Assert.Single(services.GetRequiredService<IToolPackageService>().List());

        string result = (await tools.GetTools().Single(t => t.Name == "tool_echo_file").Function.InvokeAsync(new AIFunctionArguments { ["file"] = TextFile() }))?.ToString() ?? string.Empty;

        Assert.Equal(ToolPackageState.NeedsSetup, info.State);
        Assert.Contains("about 2 GB, once", result, StringComparison.Ordinal);
        Assert.Contains("setup_tool", result, StringComparison.Ordinal);
        Assert.Contains("about 2 GB, once", tools.GetTools().Single(t => t.Name == "setup_tool").Function.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_runs_the_setup_script_once_and_a_new_version_sets_up_again()
    {
        string manifest = EchoManifest.Replace("\"risk\"", "\"setupScript\": \"setup.ps1\", \"risk\"");
        EchoTool(manifest: manifest);
        _host.WriteBundled(Path.Combine("tools", "echo-file", "setup.ps1"), """
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            New-Item -ItemType Directory -Force $env:NEXUS_TOOL_DATA | Out-Null
            Set-Content -Path (Join-Path $env:NEXUS_TOOL_DATA 'model.bin') -Value 'weights'
            @{ event = 'progress'; message = 'Downloading the model'; percent = 100 } | ConvertTo-Json -Compress
            @{ event = 'result'; text = 'ready' } | ConvertTo-Json -Compress
            """);
        await using var services = Build();
        var tools = services.GetRequiredService<IToolPackageService>();
        Assert.Equal(ToolPackageState.NeedsSetup, Assert.Single(tools.List()).State);

        var progress = new ConcurrentQueue<ToolProgress>();
        await tools.SetUpAsync("echo-file", new SynchronousProgress<ToolProgress>(progress.Enqueue));

        Assert.Equal(ToolPackageState.Ready, Assert.Single(tools.List()).State);
        Assert.Contains(progress, p => p.Message == "Downloading the model");
        Assert.True(File.Exists(Path.Combine(ToolsRoot, "data", "echo-file", "model.bin")));

        // The developer ships a new version: it needs setting up again.
        EchoTool(manifest: manifest.Replace("\"title\"", "\"version\": \"2.0.0\", \"title\""));
        tools.Reload();
        Assert.Equal(ToolPackageState.NeedsSetup, Assert.Single(tools.List()).State);

        await tools.RemoveAsync("echo-file");
        Assert.False(Directory.Exists(Path.Combine(ToolsRoot, "data", "echo-file")));
    }

    [Fact]
    public void Picking_a_tool_and_a_file_becomes_a_request_for_the_agent()
    {
        EchoTool();
        using var services = Build();

        string request = services.GetRequiredService<IToolPackageService>().RequestFor("echo-file", @"C:\Recordings\lecture 1.txt", "Then summarize it.");

        Assert.Equal("Use the Echo a file tool (tool_echo_file) on this file: \"C:\\Recordings\\lecture 1.txt\".\nThen summarize it.", request.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>The PowerShell example in Agents/ADDING_TOOLS.md, taken from the guide itself, works as written.</summary>
    [Fact]
    public async Task The_guides_example_tool_works_as_written()
    {
        string guide = File.ReadAllText(FindGuide());
        _host.WriteBundled(Path.Combine("tools", "file-hash", "tool.json"), CodeBlockAfter(guide, "`file-hash/tool.json`"));
        _host.WriteBundled(Path.Combine("tools", "file-hash", "run.ps1"), CodeBlockAfter(guide, "`file-hash/run.ps1`"));
        string file = Path.Combine(_host.Root, "تقرير.txt");
        File.WriteAllText(file, "hello");
        await using var services = Build();
        var function = services.GetRequiredService<ToolPackageToolProvider>().GetTools().Single(t => t.Name == "tool_file_hash").Function;

        string sha = (await function.InvokeAsync(new AIFunctionArguments { ["file"] = file }))?.ToString() ?? string.Empty;
        string md5 = (await function.InvokeAsync(new AIFunctionArguments { ["file"] = file, ["algorithm"] = "md5" }))?.ToString() ?? string.Empty;
        string missing = (await function.InvokeAsync(new AIFunctionArguments { ["file"] = file, ["algorithm"] = "CRC" }))?.ToString() ?? string.Empty;

        Assert.Contains("SHA256 of تقرير.txt (5 bytes): 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", sha, StringComparison.Ordinal);
        Assert.Contains("MD5 of تقرير.txt (5 bytes): 5d41402abc4b2a76b9719d911017c592", md5, StringComparison.Ordinal);
        Assert.StartsWith("Not run:", missing, StringComparison.Ordinal);
    }

    private static string FindGuide()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "ADDING_TOOLS.md");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Agents/ADDING_TOOLS.md was not found above the test folder.");
    }

    /// <summary>The first fenced code block after <paramref name="marker"/>.</summary>
    private static string CodeBlockAfter(string markdown, string marker)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int at = Array.FindIndex(lines, l => l.Trim() == marker);
        Assert.True(at >= 0, $"{marker} is not in the guide.");
        int open = Array.FindIndex(lines, at + 1, l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        int close = Array.FindIndex(lines, open + 1, l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        return string.Join("\n", lines[(open + 1)..close]);
    }

    [Fact]
    public void Tool_progress_is_narrated()
    {
        Assert.Equal("Transcribe: Transcribing (40%)",
            AgentProgressNarrator.Describe(new AgentProgress(AgentProgressKind.ToolProgress, "Transcribing (40%)") { ToolName = "Transcribe" }, NarrationLanguage.English));
        Assert.Equal("Alpha: using the transcribe tool.",
            AgentProgressNarrator.Describe(new AgentProgress(AgentProgressKind.ToolStarted, "x", "Alpha") { ToolName = "tool_transcribe" }, NarrationLanguage.English));
    }

    private sealed class SynchronousProgress(Action<AgentProgress> report) : IProgress<AgentProgress>
    {
        public void Report(AgentProgress value) => report(value);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
