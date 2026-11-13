using NexusShell.Agents.Domain.Mcp;
using NexusShell.Agents.Domain.Models;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Tests.Domain;

public class ModelReferenceTests
{
    [Theory]
    [InlineData(null, "auto", null)]
    [InlineData("", "auto", null)]
    [InlineData("AUTO", "auto", null)]
    [InlineData("openai/gpt-5-mini", "openai", "gpt-5-mini")]
    [InlineData("Ollama", "ollama", null)]
    [InlineData("openrouter/meta-llama/llama-3.3-70b-instruct", "openrouter", "meta-llama/llama-3.3-70b-instruct")]
    [InlineData("Azure OpenAI/my-deployment", "azure-openai", "my-deployment")]
    public void Parses_provider_and_model(string? text, string provider, string? model)
    {
        Assert.True(ModelReference.TryParse(text, out var reference));
        Assert.Equal(provider, reference.ProviderId);
        Assert.Equal(model, reference.ModelId);
    }

    [Theory]
    [InlineData("/gpt")]
    [InlineData("openai/")]
    [InlineData("---")]
    public void Rejects_invalid_references(string text) =>
        Assert.False(ModelReference.TryParse(text, out _));
}

public class SecretReferenceTests
{
    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("_private")]
    [InlineData("a1")]
    public void Accepts_environment_variable_names(string name) => Assert.Equal(name, new SecretReference(name).Name);

    [Theory]
    [InlineData("")]
    [InlineData("1ABC")]
    [InlineData("MY-KEY")]
    [InlineData("value-with-dashes")]
    [InlineData("WITH SPACE")]
    public void Rejects_values_that_are_not_names(string name) =>
        Assert.Throws<ArgumentException>(() => new SecretReference(name));

    [Fact]
    public void ToString_never_contains_a_value() =>
        Assert.Equal("${env:GROQ_API_KEY}", new SecretReference("GROQ_API_KEY").ToString());
}

public class SecretTemplateTests
{
    [Fact]
    public void Expands_references_and_reports_missing()
    {
        var values = new Dictionary<string, string> { ["TOKEN_A"] = "aaa" };
        string result = SecretTemplate.Expand("Bearer ${env:TOKEN_A} ${env:TOKEN_B}", r => values.GetValueOrDefault(r.Name), out var missing);

        Assert.Equal("Bearer aaa ", result);
        Assert.Equal("TOKEN_B", Assert.Single(missing).Name);
    }

    [Theory]
    [InlineData("GITHUB_TOKEN", "literal-token-value", true)]
    [InlineData("API_KEY", "${env:API_KEY}", false)]
    [InlineData("LOG_LEVEL", "debug", false)]
    [InlineData("Authorization", "Bearer abc", true)]
    public void Detects_literal_secrets(string name, string value, bool expected) =>
        Assert.Equal(expected, SecretTemplate.IsLiteralSecret(name, value));
}

public class McpServerDefinitionTests
{
    [Fact]
    public void Literal_token_in_env_is_rejected()
    {
        var server = new McpServerDefinition
        {
            Name = "github",
            Command = "npx",
            Environment = new Dictionary<string, string> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "literal-token-value" },
        };

        Assert.Contains(server.Validate(), e => e.Contains("literal secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Environment_reference_token_is_accepted()
    {
        var server = new McpServerDefinition
        {
            Name = "github",
            Command = "npx",
            Environment = new Dictionary<string, string> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "${env:GITHUB_PERSONAL_ACCESS_TOKEN}" },
        };

        Assert.Empty(server.Validate());
    }

    [Fact]
    public void Remote_http_server_must_use_https()
    {
        var server = new McpServerDefinition { Name = "remote", Transport = McpTransportKind.Http, Url = new Uri("http://mcp.invalid/mcp") };
        Assert.Contains(server.Validate(), e => e.Contains("https", StringComparison.Ordinal));
    }

    [Fact]
    public void Inline_credential_argument_is_rejected()
    {
        var server = new McpServerDefinition { Name = "x", Command = "tool", Arguments = ["--api-key=literal-key-value"] };
        Assert.NotEmpty(server.Validate());
    }
}
