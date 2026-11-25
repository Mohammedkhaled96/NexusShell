using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.ToolPackages;
using NexusShell.Agents.Domain.ToolPackages;

namespace NexusShell.Agents.Infrastructure.ToolPackages;

/// <summary>Where tool packages keep what they install and write (outside the program folder).</summary>
public sealed class ToolPackageOptions
{
    /// <summary>Python runtimes, each package's environment, downloads (models) and caches.</summary>
    public string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusShell", "tools");

    /// <summary>Results go to a folder per tool under here unless the user picks one.</summary>
    public string OutputRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NexusShell");

    /// <summary>How long a setup may take (large libraries and models on a slow line).</summary>
    public TimeSpan SetupTimeout { get; set; } = TimeSpan.FromHours(2);
}

/// <summary>
/// Installs and runs tool packages. Python packages get their own environment, created on first
/// use with uv (Python and libraries come from python.org builds on GitHub and PyPI, nothing from
/// the developer's machine); PowerShell packages need nothing. Scripts talk to NexusShell with
/// one JSON object per output line: {"event":"progress"|"result"|"error", ...}.
/// </summary>
internal sealed class ToolPackageRuntime(
    ToolPackageOptions options,
    Configuration.NexusAgentPathsOptions paths,
    UvBootstrapper uv,
    ILogger<ToolPackageRuntime> logger) : IToolPackageRuntime
{
    private const string ReadyFile = "nexus-ready.json";
    private const int MaxTextChars = 200_000;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _setupGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _settingUp = new(StringComparer.OrdinalIgnoreCase);

    public ToolPackageState GetState(ToolPackage package)
    {
        if (_settingUp.ContainsKey(package.Name)) return ToolPackageState.SettingUp;
        if (!NeedsSetup(package)) return ToolPackageState.Ready;
        try
        {
            string marker = Path.Combine(EnvironmentFolder(package), ReadyFile);
            if (!File.Exists(marker)) return ToolPackageState.NeedsSetup;
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            bool current = json.RootElement.TryGetProperty("fingerprint", out var f) && f.GetString() == Fingerprint(package);
            bool interpreter = package.Runtime != ToolRuntimeKind.Python || File.Exists(PythonExe(package));
            return current && interpreter ? ToolPackageState.Ready : ToolPackageState.NeedsSetup;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return ToolPackageState.NeedsSetup;
        }
    }

    public string StorageFolder(ToolPackage package) => Path.Combine(options.Root, "envs", package.Name);

    public string OutputFolder(ToolPackage package) => Path.Combine(options.OutputRoot, package.Name);

    public async Task SetUpAsync(ToolPackage package, IProgress<ToolProgress>? progress, CancellationToken cancellationToken)
    {
        var gate = _setupGates.GetOrAdd(package.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _settingUp[package.Name] = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.SetupTimeout);
            var token = timeout.Token;

            string environment = EnvironmentFolder(package);
            TryDelete(Path.Combine(environment, ReadyFile)); // half set up counts as not set up

            if (package.Runtime == ToolRuntimeKind.Python)
            {
                string uvExe = await uv.EnsureAsync(progress, token).ConfigureAwait(false);
                progress?.Report(new ToolProgress($"Creating the Python {package.PythonVersion} environment"));
                await RunUvAsync(uvExe, ["venv", environment, "--python", package.PythonVersion, "--clear", "--no-project"], progress, token).ConfigureAwait(false);

                int number = 0;
                foreach (var step in package.InstallSteps)
                {
                    number++;
                    progress?.Report(new ToolProgress($"Installing libraries, step {number} of {package.InstallSteps.Count}"));
                    var arguments = new List<string> { "pip", "install", "--python", PythonExe(package) };
                    if (step.IndexUrl is { Length: > 0 } index) arguments.AddRange(["--index-url", index]);
                    if (step.Requirements is { Length: > 0 } requirements) arguments.AddRange(["-r", Path.Combine(package.Directory, requirements)]);
                    arguments.AddRange(step.Packages);
                    await RunUvAsync(uvExe, arguments, progress, token).ConfigureAwait(false);
                }
            }

            if (package.SetupScript is { Length: > 0 } setup)
            {
                progress?.Report(new ToolProgress("Running the tool's setup"));
                var result = await RunScriptAsync(package, setup, new Dictionary<string, object?>(), progress, options.SetupTimeout, token).ConfigureAwait(false);
                if (!result.Succeeded) throw new InvalidOperationException(result.Text);
            }

            Directory.CreateDirectory(environment);
            await File.WriteAllTextAsync(Path.Combine(environment, ReadyFile),
                JsonSerializer.Serialize(new { fingerprint = Fingerprint(package), version = package.Version, at = DateTimeOffset.UtcNow }), token).ConfigureAwait(false);
            progress?.Report(new ToolProgress("Ready", 100));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The setup of {package.Title} did not finish within {options.SetupTimeout.TotalMinutes:0} minutes.");
        }
        finally
        {
            _settingUp.TryRemove(package.Name, out _);
            gate.Release();
        }
    }

    public async Task<ToolRunResult> RunAsync(ToolPackage package, IReadOnlyDictionary<string, object?> arguments, IProgress<ToolProgress>? progress, CancellationToken cancellationToken)
    {
        if (GetState(package) != ToolPackageState.Ready)
            return new ToolRunResult(false, $"{package.Title} is not set up yet.", []);
        return await RunScriptAsync(package, package.Entry, arguments, progress, TimeSpan.FromMinutes(package.TimeoutMinutes), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(ToolPackage package, CancellationToken cancellationToken)
    {
        if (_settingUp.ContainsKey(package.Name)) throw new InvalidOperationException($"{package.Title} is being set up right now.");
        foreach (string folder in new[] { EnvironmentFolder(package), DataFolder(package) })
        {
            if (!Directory.Exists(folder)) continue;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 3)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false); // a file still held by an exiting process
                }
            }
        }
    }

    // ---------- Running scripts ----------

    private async Task<ToolRunResult> RunScriptAsync(
        ToolPackage package, string script, IReadOnlyDictionary<string, object?> arguments,
        IProgress<ToolProgress>? progress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string scriptPath = Path.Combine(package.Directory, script);
        string output = OutputFolder(package);
        string data = DataFolder(package);
        Directory.CreateDirectory(data);
        string argumentsFile = Path.Combine(Path.GetTempPath(), $"nexus-tool-{package.Name}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(argumentsFile, JsonSerializer.Serialize(arguments), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = package.Directory,
        };
        if (package.Runtime == ToolRuntimeKind.Python)
        {
            start.FileName = PythonExe(package);
            foreach (string a in new[] { "-X", "utf8", "-B", scriptPath }) start.ArgumentList.Add(a);
            start.Environment.Remove("PYTHONHOME");
            start.Environment["PYTHONPATH"] = Path.Combine(paths.BundledToolsDirectory, "_sdk"); // nexus_tool.py, for every package
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1"; // never write into the program folder
            start.Environment["PYTHONNOUSERSITE"] = "1";
            start.Environment["PYTHONUNBUFFERED"] = "1";
        }
        else
        {
            start.FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
            foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }) start.ArgumentList.Add(a);
        }
        start.Environment["NEXUS_TOOL_ARGS"] = argumentsFile;
        start.Environment["NEXUS_TOOL_DIR"] = package.Directory;
        start.Environment["NEXUS_TOOL_DATA"] = data;
        start.Environment["NEXUS_TOOL_OUTPUT"] = output;

        logger.LogInformation("Tool {Tool}: running {Script}.", package.Name, script);
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            File.Delete(argumentsFile);
            return new ToolRunResult(false, $"Could not start {Path.GetFileName(start.FileName)}: {ex.Message}", []);
        }
        process.StandardInput.Close();

        string? resultText = null;
        string? errorText = null;
        var files = new List<string>();
        var errors = new Queue<string>();
        var plainOutput = new StringBuilder();

        var readOut = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!TryEvent(line, out var e))
                {
                    if (plainOutput.Length < MaxTextChars) plainOutput.AppendLine(line);
                    continue;
                }
                switch (e.Kind)
                {
                    case "progress":
                        progress?.Report(new ToolProgress(e.Message ?? string.Empty, e.Percent));
                        break;
                    case "result":
                        resultText = e.Text ?? string.Empty;
                        files.AddRange(e.Files);
                        break;
                    case "error":
                        errorText = e.Message;
                        break;
                }
            }
        }, CancellationToken.None);
        var readErr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (errors)
                {
                    errors.Enqueue(line);
                    if (errors.Count > 40) errors.Dequeue();
                }
            }
        }, CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            timedOut = true;
        }
        finally
        {
            TryDelete(argumentsFile);
        }

        logger.LogInformation("Tool {Tool}: {Script} finished in {Seconds:0}s (exit {Exit}).", package.Name, script, stopwatch.Elapsed.TotalSeconds, timedOut ? -1 : process.ExitCode);
        if (timedOut) return new ToolRunResult(false, $"Stopped after {timeout.TotalMinutes:0} minutes (the tool's time limit).", files);

        if (process.ExitCode == 0 && errorText is null)
            return new ToolRunResult(true, resultText ?? plainOutput.ToString().Trim(), files.Where(File.Exists).ToList());

        string tail;
        lock (errors) tail = string.Join("\n", errors);
        string reason = errorText ?? (tail.Length > 0 ? LastLines(tail, 12) : $"exit code {process.ExitCode}");
        return new ToolRunResult(false, reason, files.Where(File.Exists).ToList());
    }

    private sealed record ScriptEvent(string Kind, string? Message, double? Percent, string? Text, IReadOnlyList<string> Files);

    private static bool TryEvent(string line, out ScriptEvent result)
    {
        result = null!;
        string trimmed = line.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;
        try
        {
            using var json = JsonDocument.Parse(trimmed);
            var root = json.RootElement;
            if (!root.TryGetProperty("event", out var kind) || kind.ValueKind != JsonValueKind.String) return false;
            result = new ScriptEvent(
                kind.GetString()!.ToLowerInvariant(),
                root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                root.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? Math.Clamp(p.GetDouble(), 0, 100) : null,
                root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                    ? f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : []);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---------- uv ----------

    private async Task RunUvAsync(string uvExe, IReadOnlyList<string> arguments, IProgress<ToolProgress>? progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(uvExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["UV_CACHE_DIR"] = Path.Combine(options.Root, "cache");
        start.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(options.Root, "runtime", "python");
        start.Environment["UV_PYTHON_PREFERENCE"] = "only-managed"; // the same Python on every machine, not whatever is installed
        start.Environment["UV_LINK_MODE"] = "copy";
        start.Environment["UV_NO_CONFIG"] = "1";
        start.Environment["UV_HTTP_TIMEOUT"] = "120";
        start.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = start };
        process.Start();
        var lines = new Queue<string>();
        async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                string text = line.Trim();
                if (text.Length == 0) continue;
                lock (lines)
                {
                    lines.Enqueue(text);
                    if (lines.Count > 30) lines.Dequeue();
                }
                // "Downloading torch (202.4MiB)", "Installed 48 packages in 9.1s" ...
                if (text.StartsWith("Downloading", StringComparison.Ordinal) || text.StartsWith("Installed", StringComparison.Ordinal)
                    || text.StartsWith("Resolved", StringComparison.Ordinal) || text.StartsWith("Using CPython", StringComparison.Ordinal))
                    progress?.Report(new ToolProgress(text));
            }
        }
        var readers = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError));
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await readers.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        if (process.ExitCode != 0)
        {
            string tail;
            lock (lines) tail = string.Join("\n", lines);
            logger.LogWarning("uv {Command} failed (exit {Exit}): {Tail}", arguments[0], process.ExitCode, tail);
            throw new InvalidOperationException($"Installing failed: {LastLines(tail, 8)}");
        }
    }

    // ---------- Helpers ----------

    private static bool NeedsSetup(ToolPackage package) =>
        package.Runtime == ToolRuntimeKind.Python || package.SetupScript is not null;

    private string EnvironmentFolder(ToolPackage package) => StorageFolder(package);

    private string DataFolder(ToolPackage package) => Path.Combine(options.Root, "data", package.Name);

    private string PythonExe(ToolPackage package) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(EnvironmentFolder(package), "Scripts", "python.exe")
            : Path.Combine(EnvironmentFolder(package), "bin", "python");

    /// <summary>Changes when what must be installed changes, so an updated package is set up again.</summary>
    internal static string Fingerprint(ToolPackage package)
    {
        var text = new StringBuilder().Append(package.Version).Append('|').Append(package.Runtime).Append('|').Append(package.PythonVersion);
        foreach (var step in package.InstallSteps)
        {
            text.Append('|').Append(step.IndexUrl).Append('|').Append(string.Join(",", step.Packages));
            if (step.Requirements is { Length: > 0 } requirements)
            {
                string path = Path.Combine(package.Directory, requirements);
                text.Append('|').Append(File.Exists(path) ? File.ReadAllText(path) : requirements);
            }
        }
        if (package.SetupScript is { Length: > 0 } setup)
        {
            string path = Path.Combine(package.Directory, setup);
            text.Append('|').Append(File.Exists(path) ? File.ReadAllText(path) : setup);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    private static string LastLines(string text, int count) =>
        string.Join("\n", text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count));

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// uv (Astral's Python installer): found on PATH or in NexusShell's tools folder, otherwise downloaded
/// once from its GitHub release and checked against the published SHA-256 before it is used.
/// </summary>
internal sealed class UvBootstrapper(ToolPackageOptions options, IHttpClientFactory httpClientFactory, ILogger<UvBootstrapper> logger)
{
    internal const string Version = "0.13.8";

    /// <summary>The release's published SHA-256 of each zip (GitHub release asset digests).</summary>
    private static readonly IReadOnlyDictionary<Architecture, (string Asset, string Sha256)> Releases = new Dictionary<Architecture, (string, string)>
    {
        [Architecture.X64] = ("uv-x86_64-pc-windows-msvc.zip", "529e78a9cec6cd7e3e014f7ecd8bda0c113969d4ed0f2efeedb85c30943325b8"),
        [Architecture.Arm64] = ("uv-aarch64-pc-windows-msvc.zip", "59e6d919bed1f72035e622a7c930058f08a8931c4c78ceaf9f56e7fb74dd2c48"),
    };

    public const string HttpClientName = "nexus-agents-tools";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Installed => Path.Combine(options.Root, "runtime", "uv", "uv.exe");

    /// <summary>uv already on this machine (NexusShell's copy first), or null.</summary>
    public string? Find()
    {
        if (File.Exists(Installed)) return Installed;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(folder, "uv.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    public async Task<string> EnsureAsync(IProgress<ToolProgress>? progress, CancellationToken cancellationToken)
    {
        if (Find() is { } existing) return existing;
        if (!OperatingSystem.IsWindows() || !Releases.TryGetValue(RuntimeInformation.OSArchitecture, out var release))
            throw new PlatformNotSupportedException("Setting up Python tools needs Windows on x64 or ARM64.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(Installed)) return Installed;
            string url = $"https://github.com/astral-sh/uv/releases/download/{Version}/{release.Asset}";
            string zip = Path.Combine(Path.GetTempPath(), $"nexus-{release.Asset}");
            progress?.Report(new ToolProgress($"Downloading uv {Version} (Python installer, about 15 MB)"));
            logger.LogInformation("Downloading uv {Version} from {Url}.", Version, url);

            using (var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(zip);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            string actual;
            await using (var stream = File.OpenRead(zip))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != release.Sha256)
            {
                File.Delete(zip);
                throw new InvalidOperationException("The downloaded uv does not match its published checksum, so it was not used.");
            }

            string folder = Path.GetDirectoryName(Installed)!;
            Directory.CreateDirectory(folder);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "uv.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The uv download has no uv.exe.");
                entry.ExtractToFile(Installed, overwrite: true);
            }
            File.Delete(zip);
            return Installed;
        }
        finally
        {
            _gate.Release();
        }
    }
}
