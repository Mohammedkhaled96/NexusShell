namespace NexusShell.Agents.Domain.Providers;

/// <summary>Wire protocol family spoken by a model provider.</summary>
public enum ProviderKind
{
    /// <summary>api.openai.com.</summary>
    OpenAI,

    /// <summary>Any server implementing the OpenAI Chat Completions API
    /// (Groq, OpenRouter, Gemini, DeepSeek, Mistral, xAI, LM Studio, vLLM, llama.cpp ...).</summary>
    OpenAICompatible,

    /// <summary>Azure OpenAI / Azure AI Foundry through the OpenAI v1 endpoint.</summary>
    AzureOpenAI,

    /// <summary>Anthropic Messages API (Claude models).</summary>
    Anthropic,

    /// <summary>Ollama native API (local models).</summary>
    Ollama,
}

public static class ProviderKindParser
{
    public static bool TryParse(string? value, out ProviderKind kind)
    {
        switch (value?.Trim().ToLowerInvariant().Replace('_', '-'))
        {
            case "openai":
                kind = ProviderKind.OpenAI;
                return true;
            case "openai-compatible":
            case "openaicompatible":
            case "compatible":
                kind = ProviderKind.OpenAICompatible;
                return true;
            case "azure":
            case "azure-openai":
            case "azureopenai":
                kind = ProviderKind.AzureOpenAI;
                return true;
            case "anthropic":
            case "claude":
                kind = ProviderKind.Anthropic;
                return true;
            case "ollama":
                kind = ProviderKind.Ollama;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
