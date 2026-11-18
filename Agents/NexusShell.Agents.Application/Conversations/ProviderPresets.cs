namespace NexusShell.Agents.Application.Conversations;

/// <summary>A well-known provider the user can add with one click (then enter the key).</summary>
public sealed record ProviderPreset(
    string Id,
    string Name,
    string Kind,
    string? Endpoint,
    string? ApiKeyEnv,
    bool Local,
    string? SuggestedModel,
    string Notes);

/// <summary>
/// Catalogue of providers NexusAgent can talk to. Online providers need an API key
/// (saved as a user environment variable); local servers need no key.
/// Suggested models are optional: "Find models" lists what the provider really offers.
/// </summary>
public static class ProviderPresets
{
    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        // Online
        new("openai", "OpenAI", "openai", null, "OPENAI_API_KEY", false, "gpt-5-mini", "GPT models from OpenAI."),
        new("anthropic", "Anthropic (Claude)", "anthropic", null, "ANTHROPIC_API_KEY", false, "claude-sonnet-5-5", "Claude models."),
        new("gemini", "Google Gemini", "openai-compatible", "https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_API_KEY", false, "gemini-3.5-flash", "Free tier available; per-minute limits."),
        new("groq", "Groq", "openai-compatible", "https://api.groq.com/openai/v1", "GROQ_API_KEY", false, "openai/gpt-oss-120b", "Very fast; free tier available."),
        new("openrouter", "OpenRouter", "openai-compatible", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY", false, "openrouter/auto", "One key for many providers, including free models."),
        new("mistral", "Mistral", "openai-compatible", "https://api.mistral.ai/v1", "MISTRAL_API_KEY", false, "mistral-large-latest", "Free experiment tier available."),
        new("deepseek", "DeepSeek", "openai-compatible", "https://api.deepseek.com/v1", "DEEPSEEK_API_KEY", false, "deepseek-chat", "Low cost."),
        new("xai", "xAI (Grok)", "openai-compatible", "https://api.x.ai/v1", "XAI_API_KEY", false, null, "Use Find models to pick a Grok model."),
        new("together", "Together AI", "openai-compatible", "https://api.together.xyz/v1", "TOGETHER_API_KEY", false, null, "Many open models."),
        new("fireworks", "Fireworks AI", "openai-compatible", "https://api.fireworks.ai/inference/v1", "FIREWORKS_API_KEY", false, null, "Fast open models."),
        new("cerebras", "Cerebras", "openai-compatible", "https://api.cerebras.ai/v1", "CEREBRAS_API_KEY", false, null, "Very fast; free tier available."),
        new("github-models", "GitHub Models", "openai-compatible", "https://models.github.ai/inference", "GITHUB_TOKEN", false, null, "Use a GitHub token with the models scope."),
        new("huggingface", "Hugging Face", "openai-compatible", "https://router.huggingface.co/v1", "HF_TOKEN", false, null, "Hugging Face inference router."),
        new("nvidia", "NVIDIA NIM", "openai-compatible", "https://integrate.api.nvidia.com/v1", "NVIDIA_API_KEY", false, null, "NVIDIA hosted models."),
        new("perplexity", "Perplexity", "openai-compatible", "https://api.perplexity.ai", "PERPLEXITY_API_KEY", false, null, "Answers with web search."),
        new("azure-openai", "Azure OpenAI", "azure-openai", null, "AZURE_OPENAI_API_KEY", false, null, "Enter your resource endpoint and deployment name."),

        // Local (no key, no quota, private)
        new("ollama", "Ollama (local)", "ollama", "http://localhost:11434", null, true, null, "Install from ollama.com, then 'ollama pull llama3.2'."),
        new("lmstudio", "LM Studio (local)", "openai-compatible", "http://localhost:1234/v1", null, true, null, "Start the local server in LM Studio."),
        new("vllm", "vLLM (local)", "openai-compatible", "http://localhost:8000/v1", null, true, null, "OpenAI-compatible vLLM server."),
        new("llamacpp", "llama.cpp server (local)", "openai-compatible", "http://localhost:8080/v1", null, true, null, "llama-server with an OpenAI-compatible API."),
        new("jan", "Jan (local)", "openai-compatible", "http://localhost:1337/v1", null, true, null, "Enable the API server in Jan."),
    ];
}
