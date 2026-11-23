using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusShell.Agents.Application.Scheduling;
using NexusShell.Agents.Domain.Scheduling;

namespace NexusShell.App.ViewModels
{
    /// <summary>One scheduled task as the list shows and speaks it.</summary>
    public sealed class TaskRow
    {
        public TaskRow(ScheduledTask task, TimeZoneInfo zone)
        {
            Task = task;
            When = task.Schedule.Describe(zone);
            Next = task.Enabled && task.NextRunAt is { } next ? "next " + ChatChoice.When(next) : "turned off";
            Last = task.LastStatus switch
            {
                TaskRunStatus.Never => "not run yet",
                TaskRunStatus.Running => "running now",
                TaskRunStatus.Succeeded => "last run succeeded " + (task.LastRunAt is { } at ? ChatChoice.When(at) : string.Empty),
                TaskRunStatus.Failed => "last run failed: " + task.LastMessage,
                TaskRunStatus.Missed => "last run missed (NexusShell was closed)",
                _ => string.Empty,
            };
        }

        public ScheduledTask Task { get; }

        public string Id => Task.Id;

        public string When { get; }

        public string Next { get; }

        public string Last { get; }

        public string Text => $"{Task.Name}  -  {When}  -  {Next}";

        public string AutomationName => $"{Task.Name}, {When}, {Next}, {Last}" + (Task.CreatedBy == "agent" ? ", created by the agent" : string.Empty);
    }

    /// <summary>A weekday check box for weekly tasks.</summary>
    public sealed class DayChoice : ObservableObject
    {
        public DayChoice(DayOfWeek day) => Day = day;

        public DayOfWeek Day { get; }

        public string Name => Day.ToString();

        private bool _isChecked;
        public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
    }

    /// <summary>
    /// Scheduled tasks: the agent does something at a set time, once or repeatedly; each run is
    /// saved as a chat. Runs are unattended (actions needing approval are not taken).
    /// </summary>
    public sealed class AgentScheduleViewModel : ViewModelBase
    {
        private readonly IAgentScheduler _scheduler;

        public AgentScheduleViewModel(IAgentScheduler scheduler)
        {
            _scheduler = scheduler;
            Repeats.Add(new ChoiceItem(nameof(RepeatKind.Once), "Once", "Once"));
            Repeats.Add(new ChoiceItem(nameof(RepeatKind.Daily), "Every day", "Every day"));
            Repeats.Add(new ChoiceItem(nameof(RepeatKind.Weekdays), "Every weekday (Monday to Friday)", "Every weekday, Monday to Friday"));
            Repeats.Add(new ChoiceItem(nameof(RepeatKind.Weekly), "On chosen days of the week", "On chosen days of the week"));
            Repeats.Add(new ChoiceItem(nameof(RepeatKind.EveryMinutes), "Every few minutes or hours", "Every few minutes or hours"));
            Permissions.Add(new ChoiceItem("Ask", "Normal (commands are skipped and reported)", "Normal: commands and other approvals are skipped and reported"));
            Permissions.Add(new ChoiceItem("AutoApproveEdits", "May write files", "May write files without asking; commands are skipped and reported"));
            Permissions.Add(new ChoiceItem("ReadOnly", "Read-only", "Read-only: only reads and reports"));
            foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
                Days.Add(new DayChoice(day));
            _selectedRepeat = Repeats[1];
            _selectedPermission = Permissions[0];
            ResetForm();
            _scheduler.TasksChanged += OnTasksChanged;
        }

        public ObservableCollection<TaskRow> Tasks { get; } = new();

        public ObservableCollection<ChoiceItem> Repeats { get; } = new();

        public ObservableCollection<ChoiceItem> Permissions { get; } = new();

        public ObservableCollection<DayChoice> Days { get; } = new();

        /// <summary>Opens a chat in the chat window (set by the window that opened this one).</summary>
        public Action<string>? OpenChat { get; set; }

        private TaskRow? _selectedTask;
        public TaskRow? SelectedTask
        {
            get => _selectedTask;
            set
            {
                if (!SetProperty(ref _selectedTask, value)) return;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasResult));
                if (value is not null) FillForm(value.Task);
            }
        }

        public bool HasSelection => SelectedTask is not null;

        public bool HasResult => SelectedTask?.Task.LastChatId is not null;

        private string _name = string.Empty;
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        private string _prompt = string.Empty;
        public string Prompt { get => _prompt; set => SetProperty(ref _prompt, value); }

        private DateTime? _date;
        public DateTime? Date { get => _date; set => SetProperty(ref _date, value); }

        private string _timeText = string.Empty;
        public string TimeText { get => _timeText; set => SetProperty(ref _timeText, value); }

        private ChoiceItem _selectedRepeat;
        public ChoiceItem SelectedRepeat
        {
            get => _selectedRepeat;
            set
            {
                if (!SetProperty(ref _selectedRepeat, value ?? Repeats[0])) return;
                OnPropertyChanged(nameof(IsInterval));
                OnPropertyChanged(nameof(IsWeekly));
            }
        }

        public bool IsInterval => SelectedRepeat.Value == nameof(RepeatKind.EveryMinutes);

        public bool IsWeekly => SelectedRepeat.Value == nameof(RepeatKind.Weekly);

        private string _intervalText = "60";
        public string IntervalText { get => _intervalText; set => SetProperty(ref _intervalText, value); }

        private ChoiceItem _selectedPermission;
        public ChoiceItem SelectedPermission { get => _selectedPermission; set => SetProperty(ref _selectedPermission, value ?? Permissions[0]); }

        private string _workingFolder = string.Empty;
        public string WorkingFolder { get => _workingFolder; set => SetProperty(ref _workingFolder, value); }

        private bool _sameChat;
        public bool SameChat { get => _sameChat; set => SetProperty(ref _sameChat, value); }

        private bool _runIfMissed = true;
        public bool RunIfMissed { get => _runIfMissed; set => SetProperty(ref _runIfMissed, value); }

        private string _summary = string.Empty;
        public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

        private string _statusMessage =
            "Scheduled tasks run while NexusShell is open. A task missed while it was closed runs when it starts. Each run is saved as a chat. " +
            "Nobody may be watching, so actions that need your approval (such as commands) are skipped and reported in the result.";
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        public async Task LoadAsync()
        {
            string? selected = SelectedTask?.Id;
            var tasks = await _scheduler.ListAsync();
            Tasks.Clear();
            foreach (var task in tasks) Tasks.Add(new TaskRow(task, _scheduler.TimeZone));
            _selectedTask = Tasks.FirstOrDefault(t => t.Id == selected);
            OnPropertyChanged(nameof(SelectedTask));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasResult));
            Summary = tasks.Count == 0 ? "No scheduled tasks" : $"{tasks.Count} scheduled tasks, {tasks.Count(t => t.Enabled)} on";
        }

        private void OnTasksChanged() =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = LoadAsync()));

        public void Detach() => _scheduler.TasksChanged -= OnTasksChanged;

        public void ResetForm()
        {
            var soon = DateTime.Now.AddMinutes(10);
            Name = string.Empty;
            Prompt = string.Empty;
            Date = soon.Date;
            TimeText = soon.ToString("HH:mm", CultureInfo.InvariantCulture);
            SelectedRepeat = Repeats[1];
            IntervalText = "60";
            SelectedPermission = Permissions[0];
            WorkingFolder = string.Empty;
            SameChat = false;
            RunIfMissed = true;
            foreach (var day in Days) day.IsChecked = false;
            _selectedTask = null;
            OnPropertyChanged(nameof(SelectedTask));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasResult));
        }

        private void FillForm(ScheduledTask task)
        {
            var local = TimeZoneInfo.ConvertTime(task.Schedule.StartAt, _scheduler.TimeZone);
            Name = task.Name;
            Prompt = task.Prompt;
            Date = local.Date;
            TimeText = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            SelectedRepeat = Repeats.First(r => r.Value == task.Schedule.Repeat.ToString());
            IntervalText = task.Schedule.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
            SelectedPermission = Permissions.FirstOrDefault(p => p.Value == task.Permission) ?? Permissions[0];
            WorkingFolder = task.WorkingDirectory ?? string.Empty;
            SameChat = task.SameChat;
            RunIfMissed = task.RunIfMissed;
            foreach (var day in Days) day.IsChecked = task.Schedule.Days.Contains(day.Day);
        }

        private ScheduledTaskDraft BuildDraft()
        {
            if (Date is not DateTime date) throw new ArgumentException("Choose a date.");
            if (!DateTime.TryParseExact(TimeText.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new ArgumentException("Type the time as hours and minutes, for example 09:00 or 18:30.");

            var zone = _scheduler.TimeZone;
            var wallClock = DateTime.SpecifyKind(date.Date + time.TimeOfDay, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(wallClock)) wallClock = wallClock.AddHours(1);
            var start = new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));

            var repeat = Enum.Parse<RepeatKind>(SelectedRepeat.Value!);
            int interval = 60;
            if (repeat == RepeatKind.EveryMinutes && (!int.TryParse(IntervalText.Trim(), out interval) || interval < TaskSchedule.MinimumIntervalMinutes))
                throw new ArgumentException($"Type how many minutes between runs (at least {TaskSchedule.MinimumIntervalMinutes}; 60 = every hour).");
            var days = Days.Where(d => d.IsChecked).Select(d => d.Day).ToList();
            if (repeat == RepeatKind.Weekly && days.Count == 0) throw new ArgumentException("Tick at least one day of the week.");

            return new ScheduledTaskDraft(
                Name,
                Prompt,
                new TaskSchedule { Repeat = repeat, StartAt = start, IntervalMinutes = interval, Days = days },
                SelectedPermission.Value ?? "Ask",
                WorkingDirectory: string.IsNullOrWhiteSpace(WorkingFolder) ? null : WorkingFolder.Trim(),
                SameChat: SameChat,
                RunIfMissed: RunIfMissed);
        }

        public async Task<bool> AddAsync()
        {
            try
            {
                var task = await _scheduler.AddAsync(BuildDraft());
                await LoadAsync();
                _selectedTask = Tasks.FirstOrDefault(t => t.Id == task.Id);
                OnPropertyChanged(nameof(SelectedTask));
                OnPropertyChanged(nameof(HasSelection));
                StatusMessage = $"Scheduled \"{task.Name}\": {task.Schedule.Describe(_scheduler.TimeZone)}. Next run {ChatChoice.When(task.NextRunAt!.Value)}.";
                return true;
            }
            catch (ArgumentException ex)
            {
                StatusMessage = "Not scheduled: " + ex.Message;
                return false;
            }
        }

        public async Task<bool> SaveChangesAsync()
        {
            if (SelectedTask is null)
            {
                StatusMessage = "Choose a task in the list first, or use Add task.";
                return false;
            }
            try
            {
                var task = await _scheduler.UpdateAsync(SelectedTask.Id, BuildDraft());
                await LoadAsync();
                StatusMessage = task is null ? "That task no longer exists." : $"Saved. Next run {ChatChoice.When(task.NextRunAt!.Value)}.";
                return task is not null;
            }
            catch (ArgumentException ex)
            {
                StatusMessage = "Not saved: " + ex.Message;
                return false;
            }
        }

        public async Task RunNowAsync()
        {
            if (SelectedTask is not { } row) return;
            StatusMessage = $"Running \"{row.Task.Name}\" now. You will hear when it finishes.";
            var result = await _scheduler.RunNowAsync(row.Id);
            await LoadAsync();
            StatusMessage = result is null
                ? "That task no longer exists."
                : result.Succeeded ? $"\"{row.Task.Name}\" finished. Press Open last result to read it." : $"\"{row.Task.Name}\" failed: {result.Message}";
        }

        public async Task ToggleEnabledAsync()
        {
            if (SelectedTask is not { } row) return;
            bool enable = !row.Task.Enabled;
            await _scheduler.SetEnabledAsync(row.Id, enable);
            await LoadAsync();
            StatusMessage = enable ? $"\"{row.Task.Name}\" is on." : $"\"{row.Task.Name}\" is off; it will not run until you turn it on.";
        }

        public async Task DeleteAsync()
        {
            if (SelectedTask is not { } row) return;
            await _scheduler.DeleteAsync(row.Id);
            ResetForm();
            await LoadAsync();
            StatusMessage = $"Deleted \"{row.Task.Name}\". Its earlier results stay in Chat history.";
        }

        public bool OpenLastResult()
        {
            if (SelectedTask?.Task.LastChatId is not { } chatId || OpenChat is null) return false;
            OpenChat(chatId);
            return true;
        }

        public bool FolderExists => string.IsNullOrWhiteSpace(WorkingFolder) || Directory.Exists(WorkingFolder);
    }
}
