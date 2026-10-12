using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusShell.App.Interfaces;
using NexusShell.App.Services;
using NexusShell.App.ViewModels;
using NexusShell.App.Views;
using Serilog;
using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NexusShell.App
{
    public partial class App : Application
    {
        private readonly IHost? _host;

        public App()
        {
            // 1. إعداد نظام التسجيل (Logs)
            // سيتم حفظ السجلات في AppData/NexusShell/logs
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NexusShell",
                "logs",
                "log-.txt");

            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                // Async wrapper keeps disk I/O off the calling thread; bounded queue
                // drops the oldest event under sustained backpressure rather than blocking.
                .WriteTo.Async(a => a.File(
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"),
                    bufferSize: 10_000,
                    blockWhenFull: false)
#if DEBUG
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
#endif
                .CreateBootstrapLogger();

            try
            {
                // 2. بناء المضيف (Host Builder)
                _host = Host.CreateDefaultBuilder()
                    .ConfigureServices((context, services) =>
                    {
                        ConfigureServices(services);
                    })
                    .UseSerilog((context, services, configuration) => configuration
                        .ReadFrom.Services(services)
                        .Enrich.FromLogContext()
                        .WriteTo.Async(a => a.File(logPath, rollingInterval: RollingInterval.Day),
                            bufferSize: 10_000,
                            blockWhenFull: false)
#if DEBUG
                        .WriteTo.Console()
#endif
                        )
                    .Build();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Host terminated unexpectedly during construction.");
            }
        }

        // === 3. تسجيل الخدمات (Dependency Injection) ===
        private void ConfigureServices(IServiceCollection services)
        {
            // الخدمات الأساسية (Singletons)
            services.AddSingleton<IConfigManager, ConfigManager>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<ICommandHistory, CommandHistory>();
            services.AddSingleton<IWindowService, WindowService>();
            services.AddSingleton<IRegistryManager, RegistryManager>();
            services.AddSingleton<ISuggestionService, SuggestionService>();
            services.AddSingleton<IAIService, AIService>();

            // Named HttpClient for the AI provider with standard resilience pipeline
            // (retries + circuit-breaker + timeout) sourced from
            // Microsoft.Extensions.Http.Resilience (Polly v8 internally).
            services.AddHttpClient("groq", c =>
            {
                c.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddStandardResilienceHandler();
            services.AddSingleton<IApiTesterService, ApiTesterService>();
            
            services.AddSingleton<IOutputHistoryService, OutputHistoryService>();
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<ISoundService, SoundService>();

            // Flows (guided CLI install/uninstall)
            services.AddSingleton<IFlowLibrary, FlowLibrary>();
            services.AddSingleton<IFlowRunner, FlowRunner>();

            // Interactive Screen overlay
            services.AddSingleton<ViewModels.InteractiveScreenViewModel>();
            services.AddSingleton<IInteractiveScreenService, InteractiveScreenService>();

            // PowerShell Completion & Screen Reader Announcement
            services.AddSingleton<IPowerShellCompletionService, PowerShellCompletionService>();
            services.AddSingleton<IScreenReaderAnnouncer, ScreenReaderAnnouncer>();

            // الخدمات المؤقتة (Transients) - يتم إنشاؤها عند الطلب
            services.AddTransient<ITerminalSession, TerminalSession>();

            // الـ ViewModels
            services.AddSingleton<MainViewModel>();
            // ربط الـ ViewModel بواجهة ICommandProvider
            services.AddSingleton<ICommandProvider>(s => s.GetRequiredService<MainViewModel>());
            
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<AboutViewModel>();
            services.AddTransient<HelpViewModel>();
            services.AddTransient<ShortcutManagerViewModel>();
            services.AddTransient<SnippetManagerViewModel>();
            services.AddTransient<EnvEditorViewModel>();
            services.AddTransient<SSHManagerViewModel>();
            services.AddTransient<AliasManagerViewModel>();
            services.AddTransient<ThemeManagerViewModel>();
            services.AddTransient<ApiTesterViewModel>();

            // النوافذ (Windows)
            services.AddSingleton<MainWindow>();
            services.AddTransient<SettingsWindow>();
            services.AddTransient<AboutWindow>();
            services.AddTransient<HelpWindow>();
            services.AddTransient<ShortcutManagerWindow>();
            services.AddTransient<SnippetManagerWindow>();
            services.AddTransient<EnvEditorWindow>();
            services.AddTransient<SSHManagerWindow>();
            services.AddTransient<AliasManagerWindow>();
            services.AddTransient<ThemeManagerWindow>();
            services.AddTransient<AIResponseWindow>();
            services.AddTransient<AIChatWindow>();
            services.AddTransient<ApiTesterWindow>();
            services.AddTransient<ApiResponseWindow>();
        }

        // === 4. نقطة الانطلاق (Startup) ===
        protected override async void OnStartup(StartupEventArgs e)
        {
            if (_host == null)
            {
                Log.Fatal("Host was not created. Application cannot start.");
                Shutdown();
                return;
            }

            // معالجة الأخطاء العامة
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            await _host.StartAsync();
            Log.Information("Application starting up.");

            // Sync Directory Context Menu
            var configManager = _host.Services.GetRequiredService<IConfigManager>();
            var registryManager = _host.Services.GetRequiredService<IRegistryManager>();
            var settings = configManager.Load();

            // Apply persisted sound preferences and warm up the players so the
            // first effect has no open-file latency.
            var soundService = _host.Services.GetRequiredService<ISoundService>();
            soundService.ApplySettings(settings);
            soundService.Preload();

            // Warm up PowerShell Completion Runspace in background
            var psCompletion = _host.Services.GetRequiredService<IPowerShellCompletionService>();
            _ = Task.Run(async () => await psCompletion.InitializeAsync());

            // Universal click sound for every button/checkbox/radio in every window.
            RegisterGlobalUiSounds(soundService);

            // Apply visual-effects preference (also honours OS reduce-motion / software
            // rendering) and load the button micro-interaction styles only when enabled.
            VisualEffects.ApplySettings(settings);
            if (VisualEffects.Enabled)
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Themes/MicroInteractions.xaml")
                });
            }

            if (settings.EnableDirectoryContext)
            {
                if (!registryManager.IsDirectoryContextRegistered())
                {
                    try { registryManager.RegisterDirectoryContext(); } catch { }
                }
            }
            else
            {
                if (registryManager.IsDirectoryContextRegistered())
                {
                    try { registryManager.UnregisterDirectoryContext(); } catch { }
                }
            }

            // طلب النافذة الرئيسية من الحاوية وعرضها
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // Check for command line arguments (File/Directory Association)
            if (e.Args.Length > 0)
            {
                var path = e.Args[0];
                var viewModel = _host.Services.GetRequiredService<MainViewModel>();
                viewModel.OpenInPath(path);
            }

            base.OnStartup(e);
        }

        // Registers a single process-wide handler so EVERY button, checkbox and
        // radio button in EVERY window emits a click sound, chosen by the button's
        // role (text/name). Non-button surfaces (typing, command send/complete,
        // suggestions, list selection, window open/close) are wired at their source.
        private void RegisterGlobalUiSounds(ISoundService sound)
        {
            EventManager.RegisterClassHandler(
                typeof(System.Windows.Controls.Primitives.ButtonBase),
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                new RoutedEventHandler((s, _) =>
                {
                    // Scrollbar / slider repeat buttons raise Click continuously while
                    // held — they are not discrete user actions, so skip them.
                    if (s is System.Windows.Controls.Primitives.RepeatButton) return;
                    if (s is not System.Windows.Controls.Primitives.ButtonBase b) return;

                    sound.Play(ClassifyButtonSound(b));
                }),
                handledEventsToo: true);
        }

        // Maps a button to a sound by inspecting its text / name / accessible name.
        // Every button gets SOME sound — order matters (first match wins).
        private static AppSound ClassifyButtonSound(System.Windows.Controls.Primitives.ButtonBase b)
        {
            string text = ((b.Content as string) ?? string.Empty) + " " +
                          (b.Name ?? string.Empty) + " " +
                          (System.Windows.Automation.AutomationProperties.GetName(b) ?? string.Empty);
            text = text.ToLowerInvariant();

            // Destructive / dismiss: cancel, close, clear, delete, stop, etc.
            // (A close that also shuts a window is de-duped by CloseWindow's replay gap.)
            if (Mentions(text, "cancel", "close", "dismiss", "exit", "quit", "back",
                               "clear", "delete", "remove", "trash", "discard", "reset", "stop"))
                return AppSound.CloseWindow;

            // Submit: send/run a request (AI chat, API tester, etc.) — the send blip.
            if (Mentions(text, "send", "submit", "execute"))
                return AppSound.CommandSent;

            // Creation: new tab, add connection/alias/snippet/shortcut/variable.
            if (Mentions(text, "add", "new", "create", "insert"))
                return AppSound.NewTab;

            // Confirmation / success.
            if (Mentions(text, "save", "ok", "apply", "connect", "confirm", "yes",
                               "update", "generate", "test"))
                return AppSound.CommandCompleted;

            // Everything else (toggles, radios, dropdown openers, misc buttons): a tick.
            return AppSound.Toggle;
        }

        private static bool Mentions(string haystack, params string[] needles)
        {
            foreach (var n in needles)
                if (haystack.Contains(n, StringComparison.Ordinal)) return true;
            return false;
        }

        // معالجة الأخطاء غير المتوقعة
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Fatal(e.Exception, "An unhandled UI exception occurred");
            MessageBox.Show("An unrecoverable error occurred. The application will now shut down. See logs for details.", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            Shutdown();
        }

        // التنظيف عند الإغلاق
        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                var viewModel = _host.Services.GetRequiredService<MainViewModel>();
                viewModel.Cleanup();

                using (_host)
                {
                    await _host.StopAsync(TimeSpan.FromSeconds(5));
                }
            }

            Log.Information("Application shutting down.");
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}