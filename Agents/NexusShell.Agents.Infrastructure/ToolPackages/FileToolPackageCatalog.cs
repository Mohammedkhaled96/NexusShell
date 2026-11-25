using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.ToolPackages;
using NexusShell.Agents.Domain.ToolPackages;
using NexusShell.Agents.Domain.Tools;
using NexusShell.Agents.Infrastructure.Configuration;

namespace NexusShell.Agents.Infrastructure.ToolPackages;

/// <summary>
/// Tool packages are folders with a tool.json under <c>&lt;app&gt;\Agent\tools</c> (shipped with
/// NexusShell) and <c>%AppData%\NexusShell\agent\tools</c> (added by the user; same name wins).
/// Every package is checked before it is offered: a wrong one is listed in <see cref="Problems"/>
/// and skipped, never half-loaded. Folders starting with "_" are shared helpers, not tools.
/// </summary>
internal sealed partial class FileToolPackageCatalog(NexusAgentPathsOptions paths, ILogger<FileToolPackageCatalog> logger) : IToolPackageCatalog
{
    public const string ManifestFile = "tool.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private IReadOnlyList<ToolPackage>? _packages;
    private IReadOnlyList<string> _problems = [];

    public IReadOnlyList<ToolPackage> List()
    {
        lock (_gate)
        {
            if (_packages is null) Load();
            return _packages!;
        }
    }

    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_gate)
            {
                if (_packages is null) Load();
                return _problems;
            }
        }
    }

    public void Reload()
    {
        lock (_gate) _packages = null;
    }

    private void Load()
    {
        var packages = new Dictionary<string, ToolPackage>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var (root, bundled) in new[] { (paths.BundledToolsDirectory, true), (paths.UserToolsDirectory, false) })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('_')).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{root}: {ex.Message}");
                continue;
            }

            foreach (string folder in folders)
            {
                string manifest = Path.Combine(folder, ManifestFile);
                if (!File.Exists(manifest)) continue;
                try
                {
                    var package = Read(folder, bundled);
                    packages[package.Name] = package; // a user package with the same name replaces the bundled one
                }
                catch (Exception ex) when (ex is FormatException or JsonException or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{Path.GetFileName(folder)}: {ex.Message}");
                    logger.LogWarning("Tool package {Folder} skipped: {Reason}", folder, ex.Message);
                }
            }
        }
        _packages = packages.Values.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        _problems = problems;
        logger.LogInformation("Tool packages: {Tools}.", _packages.Count == 0 ? "none" : string.Join(", ", _packages.Select(p => p.Name)));
    }

    /// <exception cref="FormatException">The manifest is wrong (the message says what to fix).</exception>
    internal static ToolPackage Read(string folder, bool bundled)
    {
        folder = Path.GetFullPath(folder);
        var dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(Path.Combine(folder, ManifestFile)), Json)
            ?? throw new FormatException("tool.json is empty.");

        string name = Required(dto.Name, "name").ToLowerInvariant();
        if (!NamePattern().IsMatch(name)) throw new FormatException($"name \"{name}\" must be 2-40 small letters, digits or -, starting with a letter.");
        if (!string.Equals(name, Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"the folder must be named like the tool (\"{name}\").");

        var runtime = (dto.Runtime ?? "python").Trim().ToLowerInvariant() switch
        {
            "python" => ToolRuntimeKind.Python,
            "powershell" => ToolRuntimeKind.PowerShell,
            var other => throw new FormatException($"runtime \"{other}\" is not supported (python or powershell)."),
        };
        string entry = Inside(folder, Required(dto.Entry, "entry"), "entry");
        string? setup = dto.SetupScript is { Length: > 0 } s ? Inside(folder, s, "setupScript") : null;
        string expectedExtension = runtime == ToolRuntimeKind.Python ? ".py" : ".ps1";
        foreach (string script in new[] { entry, setup }.OfType<string>())
        {
            if (!string.Equals(Path.GetExtension(script), expectedExtension, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"{Path.GetFileName(script)} must be a {expectedExtension} script for the {runtime} runtime.");
        }

        var steps = new List<ToolInstallStep>();
        foreach (var step in dto.Install ?? [])
        {
            if (runtime != ToolRuntimeKind.Python) throw new FormatException("install steps are only for the python runtime.");
            var packages = (step.Packages ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            foreach (string package in packages)
            {
                if (!RequirementPattern().IsMatch(package)) throw new FormatException($"\"{package}\" is not a package name with an optional version.");
            }
            string? requirements = step.Requirements is { Length: > 0 } r ? Inside(folder, r, "requirements") : null;
            if (packages.Count == 0 && requirements is null) throw new FormatException("an install step needs packages or a requirements file.");
            if (step.IndexUrl is { Length: > 0 } index && (!Uri.TryCreate(index, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                throw new FormatException($"indexUrl \"{index}\" must be an https address.");
            steps.Add(new ToolInstallStep(packages, requirements is null ? null : Path.GetRelativePath(folder, requirements), step.IndexUrl));
        }

        var parameters = new List<ToolParameter>();
        foreach (var p in dto.Parameters ?? [])
        {
            string parameterName = Required(p.Name, "parameter name");
            if (!ParameterPattern().IsMatch(parameterName)) throw new FormatException($"parameter \"{parameterName}\" must be small letters, digits or _.");
            if (parameters.Any(x => x.Name == parameterName)) throw new FormatException($"parameter \"{parameterName}\" appears twice.");
            var kind = (p.Type ?? "text").Trim().ToLowerInvariant() switch
            {
                "file" => ToolParameterKind.File,
                "folder" => ToolParameterKind.Folder,
                "text" or "string" => ToolParameterKind.Text,
                "boolean" or "bool" => ToolParameterKind.Boolean,
                "integer" or "number" => ToolParameterKind.Integer,
                "choice" => ToolParameterKind.Choice,
                var other => throw new FormatException($"parameter \"{parameterName}\" has an unknown type \"{other}\"."),
            };
            var choices = (p.Choices ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (kind == ToolParameterKind.Choice && choices.Count == 0) throw new FormatException($"parameter \"{parameterName}\" is a choice but lists no choices.");
            var extensions = (p.Extensions ?? []).Select(e => e.Trim().StartsWith('.') ? e.Trim().ToLowerInvariant() : "." + e.Trim().ToLowerInvariant()).ToList();
            parameters.Add(new ToolParameter
            {
                Name = parameterName,
                Kind = kind,
                Description = p.Description?.Trim() ?? string.Empty,
                Required = p.Required,
                Default = p.Default?.ToString(),
                Choices = choices,
                Extensions = extensions,
            });
        }

        var risk = (dto.Risk ?? "read").Trim().ToLowerInvariant() switch
        {
            "read" => ToolRisk.ReadOnly,
            "network" => ToolRisk.Network,
            "change" => ToolRisk.Mutating,
            "run" => ToolRisk.Destructive,
            var other => throw new FormatException($"risk \"{other}\" is unknown (read, network, change or run)."),
        };
        int timeout = dto.TimeoutMinutes ?? 30;
        if (timeout is < 1 or > 1440) throw new FormatException("timeoutMinutes must be between 1 and 1440.");

        string skill = Path.Combine(folder, "SKILL.md");
        return new ToolPackage
        {
            Name = name,
            Title = Required(dto.Title, "title"),
            Description = Required(dto.Description, "description"),
            Version = dto.Version?.Trim() is { Length: > 0 } version ? version : "1.0.0",
            Directory = folder,
            Runtime = runtime,
            Entry = Path.GetRelativePath(folder, entry),
            SetupScript = setup is null ? null : Path.GetRelativePath(folder, setup),
            PythonVersion = dto.Python?.Trim() is { Length: > 0 } python && PythonPattern().IsMatch(python) ? python : "3.12",
            InstallSteps = steps,
            Parameters = parameters,
            Risk = risk,
            TimeoutMinutes = timeout,
            SetupSize = dto.SetupSize?.Trim() ?? string.Empty,
            Bundled = bundled,
            SkillFile = File.Exists(skill) ? skill : null,
        };
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new FormatException($"\"{field}\" is missing.") : value.Trim();

    /// <summary>A file of the package: inside its folder (no "..", no absolute paths) and present.</summary>
    private static string Inside(string folder, string relative, string field)
    {
        if (Path.IsPathRooted(relative)) throw new FormatException($"{field} must be a path inside the tool folder.");
        string full = Path.GetFullPath(Path.Combine(folder, relative));
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"{field} must stay inside the tool folder.");
        if (!File.Exists(full)) throw new FormatException($"{field} \"{relative}\" does not exist.");
        return full;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ParameterPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*(\[[A-Za-z0-9,_-]+\])?\s*([<>=!~]=?\s*[A-Za-z0-9.*+!-]+\s*(,\s*[<>=!~]=?\s*[A-Za-z0-9.*+!-]+\s*)*)?$")]
    private static partial Regex RequirementPattern();

    [GeneratedRegex(@"^3\.\d{1,2}$")]
    private static partial Regex PythonPattern();

    private sealed class ManifestDto
    {
        public string? Name { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public string? Runtime { get; set; }
        public string? Python { get; set; }
        public string? Entry { get; set; }
        public string? SetupScript { get; set; }
        public List<InstallDto>? Install { get; set; }
        public List<ParameterDto>? Parameters { get; set; }
        public string? Risk { get; set; }
        public int? TimeoutMinutes { get; set; }
        public string? SetupSize { get; set; }
    }

    private sealed class InstallDto
    {
        public List<string>? Packages { get; set; }
        public string? Requirements { get; set; }
        public string? IndexUrl { get; set; }
    }

    private sealed class ParameterDto
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? Description { get; set; }
        public bool Required { get; set; }

        [JsonConverter(typeof(AnyAsStringConverter))]
        public string? Default { get; set; }

        public List<string>? Choices { get; set; }
        public List<string>? Extensions { get; set; }
    }

    /// <summary>Lets a default be written as true, 3 or "text".</summary>
    private sealed class AnyAsStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Number => reader.TryGetInt64(out long n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException("default must be text, a number or true/false."),
        };

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
