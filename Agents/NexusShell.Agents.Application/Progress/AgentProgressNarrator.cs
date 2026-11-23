using NexusShell.Agents.Application.Abstractions.Models;

namespace NexusShell.Agents.Application.Progress;

public enum NarrationLanguage
{
    English,
    Arabic,
}

/// <summary>
/// Turns agent progress into short, natural sentences for screen readers and the
/// status line ("Windows Expert: checking system information"), in English or
/// Arabic. Tool ids such as <c>run_shell_command</c> are spoken as plain words,
/// never as code identifiers.
/// </summary>
public static class AgentProgressNarrator
{
    public static NarrationLanguage LanguageFrom(string? languageSetting) =>
        string.Equals(languageSetting?.Trim(), "Arabic", StringComparison.OrdinalIgnoreCase) ? NarrationLanguage.Arabic : NarrationLanguage.English;

    /// <summary>Sentence for a step, or null for steps that are not worth speaking (e.g. a tool finishing).</summary>
    public static string? Describe(AgentProgress step, NarrationLanguage language)
    {
        bool ar = language == NarrationLanguage.Arabic;
        string who = string.IsNullOrWhiteSpace(step.AgentName) ? (ar ? "الوكيل" : "The agent") : step.AgentName!;
        var tool = Tool(step.ToolName, language);

        return step.Kind switch
        {
            AgentProgressKind.Routing => ar ? "جارٍ اختيار أفضل الخبراء." : "Choosing the best experts.",
            AgentProgressKind.ExpertsSelected when step.Experts.Count > 0 =>
                ar ? $"جارٍ سؤال {Join(step.Experts, language)}." : $"Asking {Join(step.Experts, language)}.",
            AgentProgressKind.ExpertsSelected => null,
            AgentProgressKind.ExpertStarted => ar ? $"{who} يعمل الآن." : $"{who} is working.",
            AgentProgressKind.ExpertCompleted => ar ? $"انتهى {who}." : $"{who} finished.",
            AgentProgressKind.SubAgentStarted => ar ? $"جارٍ طلب المساعدة من {who}." : $"Asking {who} for help.",
            AgentProgressKind.ToolStarted => ar ? $"{who}: جارٍ {tool.Noun}." : $"{who}: {tool.Progressive}.",
            AgentProgressKind.ToolCompleted => null,
            AgentProgressKind.ToolFailed => ar ? $"{who}: فشل {tool.Noun}." : $"{who}: {tool.Noun} failed.",
            AgentProgressKind.ApprovalRequested =>
                ar ? $"{who} يحتاج موافقتك على {tool.Noun}." : $"{who} needs your approval to {tool.Base}.",
            AgentProgressKind.ModelUnavailable => ModelProblem(who, step, ar),
            AgentProgressKind.FallbackExpert => ar ? $"جارٍ تجربة {who} بدلًا من ذلك." : $"Trying {who} instead.",
            AgentProgressKind.WaitingForQuota => ar
                ? $"{who}: كل المزوّدين وصلوا إلى الحد، الانتظار {Seconds(step.Delay)} ثانية ثم إعادة المحاولة."
                : $"{who}: every provider is at its limit, waiting {Seconds(step.Delay)} seconds, then trying again.",
            AgentProgressKind.Synthesizing => ar ? "جارٍ دمج الإجابات." : "Combining the answers.",
            AgentProgressKind.MemoryRecalled => step.Count is int n && n > 0
                ? (ar ? $"{who}: يستخدم {n} من المعلومات المحفوظة." : $"{who}: using {n} saved {(n == 1 ? "memory" : "memories")}.")
                : null,
            AgentProgressKind.MemorySaved => ar ? "تم حفظ معلومة في الذاكرة." : "Saved to memory.",
            AgentProgressKind.CompactingHistory => ar ? "جارٍ تلخيص بداية المحادثة للاحتفاظ بها." : "Summarizing the earlier conversation to keep it in mind.",
            _ => string.IsNullOrWhiteSpace(step.Message) ? null : step.Message,
        };
    }

    /// <summary>Says the real reason a model or provider was dropped (quota, unreachable, key, retired, slow).</summary>
    private static string ModelProblem(string who, AgentProgress step, bool ar)
    {
        string provider = string.IsNullOrWhiteSpace(step.Provider) ? (ar ? "المزوّد" : "the provider") : step.Provider!;
        return step.Failure switch
        {
            ModelFailureKind.RateLimited => ar
                ? $"{who}: انتهت حصة {provider}، جارٍ التبديل إلى مزوّد آخر."
                : $"{who}: {provider} quota is used up, switching to another provider.",
            ModelFailureKind.Unreachable => ar
                ? $"{who}: لا يمكن الوصول إلى {provider} من هذا الجهاز، جارٍ التبديل إلى مزوّد آخر."
                : $"{who}: {provider} cannot be reached from this computer, switching to another provider.",
            ModelFailureKind.Unauthorized => ar
                ? $"{who}: رفض {provider} مفتاح الـ API، جارٍ التبديل إلى مزوّد آخر."
                : $"{who}: {provider} rejected the API key, switching to another provider.",
            ModelFailureKind.ModelNotFound => ar
                ? $"{who}: هذا النموذج غير متاح، جارٍ التبديل إلى نموذج آخر."
                : $"{who}: that model is not available, switching to another model.",
            _ => ar
                ? $"{who}: النموذج لا يستجيب، جارٍ التبديل إلى نموذج آخر."
                : $"{who}: the model is not responding, switching to another model.",
        };
    }

    public static string Sent(NarrationLanguage language) =>
        language == NarrationLanguage.Arabic ? "تم إرسال الرسالة. الوكيل يعمل الآن." : "Message sent. NexusAgent is working.";

    public static string Answered(IReadOnlyList<string> experts, NarrationLanguage language)
    {
        bool ar = language == NarrationLanguage.Arabic;
        if (experts.Count == 0) return ar ? "الإجابة جاهزة." : "Answer ready.";
        return ar ? $"الإجابة جاهزة من {Join(experts, language)}." : $"Answer ready from {Join(experts, language)}.";
    }

    public static string NoAnswer(NarrationLanguage language) =>
        language == NarrationLanguage.Arabic ? "تعذر على الوكيل الإجابة. التفاصيل في المحادثة." : "NexusAgent could not answer. The details are in the chat.";

    public static string Stopped(NarrationLanguage language) =>
        language == NarrationLanguage.Arabic ? "تم الإيقاف." : "Stopped.";

    /// <summary>Periodic reminder during long waits, repeating the current step.</summary>
    public static string StillWorking(string? currentStep, TimeSpan elapsed, NarrationLanguage language)
    {
        int seconds = (int)Math.Round(elapsed.TotalSeconds);
        string step = string.IsNullOrWhiteSpace(currentStep) ? string.Empty : " " + currentStep!.TrimEnd('.') + ".";
        return language == NarrationLanguage.Arabic
            ? $"ما زال العمل جاريًا.{step} {seconds} ثانية."
            : $"Still working.{step} {seconds} seconds.";
    }

    /// <summary>Spoken forms of a tool: progressive ("running a command"), base ("run a command") and Arabic verbal noun.</summary>
    internal static (string Progressive, string Base, string Noun) Tool(string? toolName, NarrationLanguage language)
    {
        string name = toolName?.Trim() ?? string.Empty;
        switch (name)
        {
            case "run_shell_command": return ("running a command", "run a command", "تشغيل أمر");
            case "get_system_info": return ("checking system information", "check system information", "فحص معلومات النظام");
            case "get_terminal_output": return ("reading the terminal output", "read the terminal output", "قراءة مخرجات الطرفية");
            case "list_directory": return ("listing a folder", "list a folder", "عرض محتويات مجلد");
            case "read_text_file": return ("reading a file", "read a file", "قراءة ملف");
            case "search_files": return ("searching files", "search files", "البحث في الملفات");
            case "write_text_file": return ("writing a file", "write a file", "كتابة ملف");
            case "fetch_url": return ("reading a web page", "read a web page", "قراءة صفحة ويب");
            case "load_skill": return ("loading a skill", "load a skill", "تحميل مهارة");
            case "read_skill_resource": return ("reading skill material", "read skill material", "قراءة مرجع مهارة");
            case "run_skill_script": return ("running a skill script", "run a skill script", "تشغيل سكربت مهارة");
            case "remember": return ("saving to memory", "save to memory", "الحفظ في الذاكرة");
            case "recall_memory": return ("searching memory", "search memory", "البحث في الذاكرة");
            case "update_memory": return ("correcting a memory", "correct a memory", "تصحيح معلومة محفوظة");
            case "forget_memory": return ("deleting a memory", "delete a memory", "حذف معلومة محفوظة");
            case "schedule_task": return ("scheduling a task", "schedule a task", "جدولة مهمة");
            case "list_scheduled_tasks": return ("checking the scheduled tasks", "check the scheduled tasks", "عرض المهام المجدولة");
            case "cancel_scheduled_task": return ("cancelling a scheduled task", "cancel a scheduled task", "إلغاء مهمة مجدولة");
        }

        // Connected applications.
        foreach (var (prefix, english, arabic) in ConnectorNames)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return ($"working with {english}", $"use {english}", $"العمل مع {arabic}");
        }

        if (name.StartsWith("ask_", StringComparison.Ordinal))
        {
            string expert = Words(name[4..]);
            return ($"asking the {expert}", $"ask the {expert}", $"سؤال {expert}");
        }

        if (name.StartsWith("mcp_", StringComparison.Ordinal))
        {
            string rest = Words(name[4..]);
            return ($"using the MCP tool {rest}", $"use the MCP tool {rest}", $"استخدام أداة MCP {rest}");
        }

        string words = name.Length == 0 ? (language == NarrationLanguage.Arabic ? "أداة" : "a tool") : Words(name);
        return ($"using {words}", $"use {words}", $"استخدام {words}");
    }

    private static readonly (string Prefix, string English, string Arabic)[] ConnectorNames =
    [
        ("gmail_", "Gmail", "Gmail"),
        ("google_calendar_", "Google Calendar", "تقويم Google"),
        ("outlook_", "Outlook", "Outlook"),
        ("github_", "GitHub", "GitHub"),
    ];

    private static int Seconds(TimeSpan? delay) => (int)Math.Round((delay ?? TimeSpan.Zero).TotalSeconds);

    private static string Words(string identifier) => identifier.Replace('_', ' ').Replace('-', ' ').Trim();

    private static string Join(IReadOnlyList<string> items, NarrationLanguage language)
    {
        if (items.Count == 1) return items[0];
        string and = language == NarrationLanguage.Arabic ? " و" : " and ";
        return string.Join(", ", items.Take(items.Count - 1)) + and + items[^1];
    }
}
