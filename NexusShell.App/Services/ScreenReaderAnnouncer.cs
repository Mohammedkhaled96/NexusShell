using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NexusShell.App.Services
{
    public interface IScreenReaderAnnouncer
    {
        void Announce(string text);
    }

    public sealed class ScreenReaderAnnouncer : IScreenReaderAnnouncer
    {
        private readonly ILogger<ScreenReaderAnnouncer> _logger;

        [DllImport("nvdaControllerClient32.dll", EntryPoint = "nvdaController_speakText", CharSet = CharSet.Unicode)]
        private static extern int NvdaSpeak32(string text);

        [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_speakText", CharSet = CharSet.Unicode)]
        private static extern int NvdaSpeak64(string text);

        private readonly bool _is64Bit = Environment.Is64BitProcess;

        public static event Action<string>? AnnouncementRequested;
        private static ScreenReaderAnnouncer? _instance;

        public ScreenReaderAnnouncer(ILogger<ScreenReaderAnnouncer> logger)
        {
            _logger = logger;
            _instance = this;
        }

        public static void AnnounceCheckbox(string itemName, bool isChecked)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return;
            string clean = itemName.Trim();

            bool isArabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase)
                            || ContainsArabic(clean);

            string text = isArabic
                ? $"{clean}، {(isChecked ? "مربع اختيار محدد" : "مربع اختيار غير محدد")}"
                : $"{clean}, {(isChecked ? "check box checked" : "check box not checked")}";

            Speak(text);
        }

        private static bool ContainsArabic(string text)
        {
            foreach (char c in text)
            {
                if (c >= 0x0600 && c <= 0x06FF) return true;
            }
            return false;
        }

        public static void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            if (_instance != null)
            {
                _instance.Announce(text);
                return;
            }

            // Direct static fallback
            try
            {
                int status = Environment.Is64BitProcess ? NvdaSpeak64(text) : NvdaSpeak32(text);
                if (status == 0) return;
            }
            catch { }

            try
            {
                var jawsType = Type.GetTypeFromProgID("FreedomSci.JawsApi");
                if (jawsType != null)
                {
                    dynamic? jaws = Activator.CreateInstance(jawsType);
                    if (jaws != null)
                    {
                        var res = jaws.SayString(text, false);
                        if (res is bool b && b) return;
                    }
                }
            }
            catch { }

            AnnouncementRequested?.Invoke(text);
        }

        public void Announce(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // 1. Try direct NVDA Controller API (0ms, crystal-clear, no duplicate UIA events)
            try
            {
                int status = _is64Bit ? NvdaSpeak64(text) : NvdaSpeak32(text);
                if (status == 0) return;
            }
            catch { }

            // 2. Try JAWS COM API
            try
            {
                var jawsType = Type.GetTypeFromProgID("FreedomSci.JawsApi");
                if (jawsType != null)
                {
                    dynamic? jaws = Activator.CreateInstance(jawsType);
                    if (jaws != null)
                    {
                        var res = jaws.SayString(text, false);
                        if (res is bool b && b) return;
                    }
                }
            }
            catch { }

            // 3. Fallback to UIA Live Region ONLY if direct screen reader controller is unavailable
            AnnouncementRequested?.Invoke(text);
            _logger.LogDebug("ScreenReader Announcement: {Text}", text);
        }
    }
}
