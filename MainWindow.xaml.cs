using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nimbo;

public partial class MainWindow : Window
{
    private readonly MemoryMonitor _monitor = new();
    private readonly MemoryCleaner _cleaner = new();
    private readonly LocalStore _store = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private AppSettings _settings = new();
    private MemorySnapshot? _currentSnapshot;
    private DateTimeOffset? _lastActionAt;
    private DateTimeOffset _startedAt = DateTimeOffset.Now;
    private bool _loadingSettings = true;
    private bool _cleaning;
    private bool _draggingThreshold;
    private bool _launchMinimized;
    private bool _elevatedCleanupPending;
    private Forms.NotifyIcon? _notifyIcon;
    private int _lastTrayPercent = -1;
    private TaskCompletionSource<string>? _dialogCompletion;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.LoadSettings();
        _launchMinimized = Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        _elevatedCleanupPending = Environment.GetCommandLineArgs().Contains("--clean-once", StringComparer.OrdinalIgnoreCase);
        _lastActionAt = _store.LoadHistory().FirstOrDefault()?.StartedAt;
        LoadSettingsIntoControls();
        RenderHistory();
        UpdateElevationStatus();
        InitializeTray();
        _loadingSettings = false;
        UpdateSelectionSummary();
        UpdateAutoSummary();

        _timer.Tick += (_, _) => RefreshSnapshot();
        Loaded += (_, _) =>
        {
            RefreshSnapshot();
            _timer.Start();
            if (_launchMinimized) Hide();
            if (_elevatedCleanupPending) _ = RunCleanupAsync(isAutomatic: false);
        };
        Closed += (_, _) => { _timer.Stop(); DisposeTray(); };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SetPage("overview");
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            SetPage("settings");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DialogOverlay.Visibility == Visibility.Visible)
        {
            CompleteDialog("cancel");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _cleaning)
        {
            e.Handled = true;
        }
    }

    private void RefreshSnapshot()
    {
        try
        {
            _currentSnapshot = _monitor.GetSnapshot();
            RenderSnapshot(_currentSnapshot);
            EvaluateAutomaticCleanup(_currentSnapshot);
        }
        catch (Exception ex)
        {
            RamStateText.Text = "НЕТ ДАННЫХ";
            LastUpdatedText.Text = ex.Message;
        }
    }

    private void RenderSnapshot(MemorySnapshot snapshot)
    {
        var percent = Math.Clamp(snapshot.PhysicalUsagePercent, 0, 100);
        RamPercentText.Text = $"{percent:0.0}%";
        RamUsedText.Text = $"{FormatBytes(snapshot.PhysicalUsed)} из {FormatBytes(snapshot.PhysicalTotal)}";
        RamAvailableText.Text = $"Доступно {FormatBytes(snapshot.PhysicalAvailable)}";
        HeroAvailableText.Text = FormatBytes(snapshot.PhysicalAvailable);
        HeroUsedText.Text = FormatBytes(snapshot.PhysicalUsed);
        RamTotalText.Text = $"Всего {FormatBytes(snapshot.PhysicalTotal)}";
        PagefileUsageText.Text = $"{snapshot.PagefileUsagePercent:0.0}%";
        PagefileDetailText.Text = $"Доступно {FormatBytes(snapshot.PagefileAvailable)} из {FormatBytes(snapshot.PagefileTotal)}";
        CacheUsageText.Text = FormatBytes(snapshot.SystemCache);
        SetBarFill(RamBarTrack.ActualWidth, RamBarFill, percent);
        SetBarFill(PagefileBar.ActualWidth, PagefileFill, snapshot.PagefileUsagePercent);
        var cachePercent = snapshot.PhysicalTotal == 0 ? 0 : 100d * snapshot.SystemCache / snapshot.PhysicalTotal;
        SetBarFill(CacheBar.ActualWidth, CacheFill, cachePercent);
        LastUpdatedText.Text = snapshot.CapturedAt.ToString("HH:mm:ss");
        UpdateTrayIcon(percent);

        var severity = percent >= 90 ? "danger" : percent >= 75 ? "warning" : "normal";
        RamPercentText.Foreground = (Brush)FindResource(severity == "danger" ? "DangerBrush" : severity == "warning" ? "WarningBrush" : "AccentBrush");
        RamStateText.Text = severity == "danger" ? "ВЫСОКАЯ НАГРУЗКА" : severity == "warning" ? "НАГРУЗКА РАСТЁТ" : "НОРМА";
        RamStateText.Foreground = severity switch
        {
            "danger" => Brushes.White,
            "warning" => (Brush)FindResource("WarningBrush"),
            _ => (Brush)FindResource("InkBrush")
        };
        RamStateBadge.Background = severity == "danger"
            ? (Brush)FindResource("DangerBrush")
            : severity == "warning"
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3A291B"))
                : (Brush)FindResource("AccentBrush");
    }

    private static void SetBarFill(double availableWidth, Border fill, double value)
    {
        if (availableWidth <= 0)
            return;
        fill.Width = Math.Max(2, availableWidth * Math.Clamp(value, 0, 100) / 100);
    }

    private void LoadSettingsIntoControls()
    {
        WorkingSetsCheck.IsChecked = _settings.CleanWorkingSets;
        SystemFileCacheCheck.IsChecked = _settings.CleanSystemFileCache;
        TempFilesCheck.IsChecked = _settings.CleanUserTempFiles;
        RunAtStartupCheck.IsChecked = _settings.RunAtStartup;
        StartMinimizedCheck.IsChecked = _settings.StartMinimized;
        StartMinimizedCheck.IsEnabled = _settings.RunAtStartup;
        ThresholdEnabledCheck.IsChecked = _settings.ThresholdEnabled;
        IntervalEnabledCheck.IsChecked = _settings.IntervalEnabled;
        IntervalValueText.Text = Math.Clamp(_settings.IntervalMinutes, 10, 240).ToString();
        ThresholdSliderCanvas.IsHitTestVisible = _settings.ThresholdEnabled;
        ThresholdSliderCanvas.Opacity = _settings.ThresholdEnabled ? 1 : 0.7;
        UpdateThresholdVisual(_settings.ThresholdPercent);
    }

    private void UpdateThresholdVisual(int value)
    {
        value = Math.Clamp((int)Math.Round(value / 5d) * 5, 50, 95);
        _settings.ThresholdPercent = value;
        var position = 8 + (value - 50) / 45d * 174;
        ThresholdFill.Width = position - 8;
        Canvas.SetLeft(ThresholdThumb, position - 8);
        SettingsThresholdText.Text = $"{value}%";
    }

    private void ThresholdSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ThresholdSliderCanvas.Focus();
        _draggingThreshold = true;
        ThresholdSliderCanvas.CaptureMouse();
        SetThresholdFromPointer(e.GetPosition(ThresholdSliderCanvas).X);
        e.Handled = true;
    }

    private void ThresholdSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingThreshold && e.LeftButton == MouseButtonState.Pressed)
            SetThresholdFromPointer(e.GetPosition(ThresholdSliderCanvas).X);
    }

    private void ThresholdSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingThreshold)
            return;
        SetThresholdFromPointer(e.GetPosition(ThresholdSliderCanvas).X);
        _draggingThreshold = false;
        ThresholdSliderCanvas.ReleaseMouseCapture();
        if (!_loadingSettings)
            SaveSettings();
    }

    private void SetThresholdFromPointer(double x)
    {
        var fraction = Math.Clamp((x - 8) / 174d, 0, 1);
        var value = 50 + (int)Math.Round(fraction * 45 / 5) * 5;
        UpdateThresholdVisual(value);
    }

    private void ThresholdSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var delta = e.Key switch
        {
            Key.Left or Key.Down => -5,
            Key.Right or Key.Up => 5,
            Key.Home => -45,
            Key.End => 45,
            _ => 0
        };
        if (delta == 0)
            return;
        UpdateThresholdVisual(_settings.ThresholdPercent + delta);
        SaveSettings();
        e.Handled = true;
    }

    private void UpdateAutoSummary()
    {
        if (AutoModeSummaryText is null)
            return;
        var active = _settings.ThresholdEnabled || _settings.IntervalEnabled;
        if (!active)
            AutoModeSummaryText.Text = "Автоматический режим выключен.";
        else if (_settings.ThresholdEnabled && _settings.IntervalEnabled)
            AutoModeSummaryText.Text = $"Порог {_settings.ThresholdPercent}% или каждые {_settings.IntervalMinutes} мин.";
        else if (_settings.ThresholdEnabled)
            AutoModeSummaryText.Text = $"Запуск при загрузке выше {_settings.ThresholdPercent}%.";
        else
            AutoModeSummaryText.Text = $"Запуск каждые {_settings.IntervalMinutes} мин.";
        AutoModeSummaryText.Text += " Между операциями — не менее 10 минут.";
        ThresholdSliderCanvas.IsHitTestVisible = _settings.ThresholdEnabled;
        ThresholdSliderCanvas.Opacity = _settings.ThresholdEnabled ? 1 : 0.7;
    }

    private void UpdateSelectionSummary()
    {
        var selected = (_settings.CleanWorkingSets ? 1 : 0) + (_settings.CleanSystemFileCache ? 1 : 0) + (_settings.CleanUserTempFiles ? 1 : 0);
        SelectedAreasCountText.Text = $"{selected:00} / 03 ВЫБРАНО";
        CleanButton.IsEnabled = selected > 0 && !_cleaning;
        QuickRamCleanButton.IsEnabled = !_cleaning;
        var chosen = SelectedAreaLabels();
        CleanupSummaryText.Text = selected == 0
            ? "Выбери хотя бы одну область."
            : $"Выбрано: {string.Join(" · ", chosen)}.";
    }

    private void EvaluateAutomaticCleanup(MemorySnapshot snapshot)
    {
        if (_cleaning || (!_settings.ThresholdEnabled && !_settings.IntervalEnabled) ||
            (!_settings.CleanWorkingSets && !_settings.CleanSystemFileCache))
            return;
        var now = DateTimeOffset.Now;
        if (_lastActionAt.HasValue && now - _lastActionAt.Value < TimeSpan.FromMinutes(10))
            return;
        var thresholdReached = _settings.ThresholdEnabled && snapshot.PhysicalUsagePercent >= _settings.ThresholdPercent;
        var intervalReached = _settings.IntervalEnabled &&
            now - (_lastActionAt ?? _startedAt) >= TimeSpan.FromMinutes(Math.Clamp(_settings.IntervalMinutes, 10, 240));
        if (thresholdReached || intervalReached)
            _ = RunCleanupAsync(isAutomatic: true);
    }

    private async Task RunCleanupAsync(bool isAutomatic, bool quickRamOnly = false)
    {
        var trimWorkingSets = quickRamOnly || _settings.CleanWorkingSets;
        var trimSystemFileCache = !quickRamOnly && _settings.CleanSystemFileCache;
        var cleanTempFiles = !isAutomatic && !quickRamOnly && _settings.CleanUserTempFiles;
        if (_cleaning || (!trimWorkingSets && !trimSystemFileCache && !cleanTempFiles))
            return;
        _cleaning = true;
        CleanButton.IsEnabled = false;
        QuickRamCleanButton.IsEnabled = false;
        var record = new CleanupRecord
        {
            StartedAt = DateTimeOffset.Now,
            AvailableBefore = _currentSnapshot?.PhysicalAvailable ?? 0,
            Status = "failed"
        };
        if (trimWorkingSets)
            record.SelectedAreas.Add("Рабочие наборы приложений");
        if (trimSystemFileCache)
            record.SelectedAreas.Add("Кэш файловой системы Windows");
        if (cleanTempFiles)
            record.SelectedAreas.Add("Временные файлы пользователя");

        try
        {
            var before = _currentSnapshot ?? _monitor.GetSnapshot();
            record.AvailableBefore = before.PhysicalAvailable;
            record = await Task.Run(() => _cleaner.Clean(before, trimWorkingSets, trimSystemFileCache, cleanTempFiles));
            await Task.Delay(500);
            var after = _monitor.GetSnapshot();
            record.AvailableAfter = after.PhysicalAvailable;
            _currentSnapshot = after;
            RenderSnapshot(after);
            if (!isAutomatic)
                await ShowDialogAsync(record.Status == "completed" ? "Готово" : "Есть нюансы", ResultText(record), "Закрыть",
                    isWarning: record.Status != "completed", eyebrow: "NIMBO / РЕЗУЛЬТАТ");
        }
        catch (Exception ex)
        {
            record.FinishedAt = DateTimeOffset.Now;
            record.AvailableAfter = _currentSnapshot?.PhysicalAvailable ?? record.AvailableBefore;
            record.Note = ex.Message;
            record.Status = "failed";
            if (!isAutomatic)
                await ShowDialogAsync("Не получилось завершить", ex.Message, "Понятно", isWarning: true);
        }
        finally
        {
            record.FinishedAt = record.FinishedAt == default ? DateTimeOffset.Now : record.FinishedAt;
            _lastActionAt = record.StartedAt;
            try { _store.AppendHistory(record); }
            catch (Exception ex)
            {
                if (!isAutomatic)
                    await ShowDialogAsync("Журнал не сохранён", ex.Message, "Понятно", isWarning: true);
            }
            RenderHistory();
            _cleaning = false;
            UpdateSelectionSummary();
        }
    }

    private static string ResultText(CleanupRecord record)
    {
        var title = record.Status switch
        {
            "completed" => "Очистка завершена.",
            "partial" => "Готово частично.",
            _ => "Очистить не удалось."
        };
        return $"{title}\n\n{record.Note}\n\nОбработано процессов: {record.ProcessesTrimmed}; пропущено: {record.ProcessesSkipped}. " +
               $"Временных файлов удалено: {record.TempFilesDeleted} ({FormatBytes(record.TempBytesDeleted)}); пропущено: {record.TempFilesSkipped}. " +
               $"Файловый кэш: {(record.SystemFileCacheTrimmed ? "освобождён" : "не изменён")}. " +
               $"Доступно RAM: {FormatBytes(record.AvailableBefore)} → {FormatBytes(record.AvailableAfter)}.";
    }

    private void CleanButton_Click(object sender, RoutedEventArgs e) => _ = PromptCleanupAsync();
    private void QuickRamCleanButton_Click(object sender, RoutedEventArgs e) => _ = RunCleanupAsync(isAutomatic: false, quickRamOnly: true);

    private async Task PromptCleanupAsync()
    {
        if (_cleaning)
            return;
        if (!_settings.CleanWorkingSets && !_settings.CleanSystemFileCache && !_settings.CleanUserTempFiles)
        {
            await ShowDialogAsync("Пока нечего очищать", "Сначала выбери одну или несколько областей.", "Понятно");
            SetPage("cleaning");
            return;
        }
        var needsElevation = _settings.CleanSystemFileCache && !IsAdministrator();
        var isDestructive = _settings.CleanUserTempFiles;
        var message = BuildCleanupPrompt(needsElevation, isDestructive);
        var choice = await ShowDialogAsync(
            "Подтвердить операцию",
            message,
            needsElevation ? "Запросить права" : "Выполнить",
            needsElevation ? "Без повышения" : null,
            "Отмена",
            isDestructive,
            eyebrow: "NIMBO / ПЕРЕД ЗАПУСКОМ");
        if (choice == "cancel")
            return;
        if (choice == "secondary" || !needsElevation)
        {
            _ = RunCleanupAsync(isAutomatic: false);
            return;
        }
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к приложению.");
            Process.Start(new ProcessStartInfo(executable, "--clean-once") { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            var fallback = await ShowDialogAsync("Права не выданы", "Продолжить только с доступными областями?", "Продолжить", isWarning: true);
            if (fallback == "primary")
                _ = RunCleanupAsync(isAutomatic: false);
        }
        catch (Exception ex)
        {
            var fallback = await ShowDialogAsync("Не получилось запросить права", $"{ex.Message}\n\nПродолжить без повышения?", "Продолжить", isWarning: true);
            if (fallback == "primary")
                _ = RunCleanupAsync(isAutomatic: false);
        }
    }

    private string BuildCleanupPrompt(bool needsElevation, bool isDestructive)
    {
        var areas = string.Join("\n", SelectedAreaLabels().Select(area => $"• {area}"));
        var message = $"Выбрано:\n{areas}\n\n";
        message += isDestructive
            ? "Файлы из папки %TEMP% будут удалены сразу, минуя Корзину. Закрой программы: открытые или занятые файлы пропустим."
            : "Документы и другие пользовательские файлы не удаляются. Windows может повторно загрузить освобождённые страницы.";
        if (needsElevation)
            message += "\n\nДля системного файлового кэша можно запросить права администратора.";
        return message;
    }

    private string[] SelectedAreaLabels()
    {
        var labels = new System.Collections.Generic.List<string>();
        if (_settings.CleanWorkingSets) labels.Add("рабочие наборы приложений");
        if (_settings.CleanSystemFileCache) labels.Add("файловый кэш Windows");
        if (_settings.CleanUserTempFiles) labels.Add("временные файлы пользователя");
        return labels.ToArray();
    }

    private void CleaningArea_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || WorkingSetsCheck is null || SystemFileCacheCheck is null || TempFilesCheck is null)
            return;
        _settings.CleanWorkingSets = WorkingSetsCheck.IsChecked == true;
        _settings.CleanSystemFileCache = SystemFileCacheCheck.IsChecked == true;
        _settings.CleanUserTempFiles = TempFilesCheck.IsChecked == true;
        SaveSettings();
        UpdateSelectionSummary();
    }

    private void RenderHistory()
    {
        var records = _store.LoadHistory();
        HistoryItems.Items.Clear();
        HistoryEmptyCard.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCountText.Text = $"{records.Count} {Plural(records.Count, "запись", "записи", "записей")}";
        foreach (var record in records)
        {
            var card = new Border
            {
                Style = (Style)FindResource("CardStyle"),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var stack = new StackPanel();
            var title = new TextBlock
            {
                Text = record.Status switch
                {
                    "completed" => "Операция завершена",
                    "partial" => "Частичный результат",
                    _ => "Операция не завершилась"
                },
                Foreground = (Brush)FindResource(record.Status == "failed" ? "WarningBrush" : "TextBrush"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            var areas = record.SelectedAreas.Count == 0 ? "Области не записаны" : string.Join(" + ", record.SelectedAreas);
            var detail = new TextBlock
            {
                Text = $"{areas} · процессов: {record.ProcessesTrimmed} · доступно {FormatBytes(record.AvailableBefore)} → {FormatBytes(record.AvailableAfter)}\n{record.Note}",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            };
            stack.Children.Add(title);
            stack.Children.Add(detail);
            var date = new TextBlock
            {
                Text = record.StartedAt.ToLocalTime().ToString("dd MMM, HH:mm"),
                Foreground = (Brush)FindResource("SubtleBrush"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(14, 0, 0, 0)
            };
            Grid.SetColumn(date, 1);
            grid.Children.Add(stack);
            grid.Children.Add(date);
            card.Child = grid;
            HistoryItems.Items.Add(card);
        }
    }

    private void UpdateElevationStatus()
    {
        try
        {
            ElevationStatusText.Text = IsAdministrator()
                ? "Права администратора доступны."
                : "Для системного кэша запроси повышение прав при запуске очистки.";
        }
        catch
        {
            ElevationStatusText.Text = "Права администратора не определены.";
        }
    }

    private async Task<string> ShowDialogAsync(
        string title,
        string message,
        string primary,
        string? secondary = null,
        string cancel = "Отмена",
        bool isDestructive = false,
        bool isWarning = false,
        string eyebrow = "NIMBO / СООБЩЕНИЕ")
    {
        if (_dialogCompletion is not null)
            return "cancel";

        DialogEyebrowText.Text = eyebrow;
        DialogTitleText.Text = title;
        DialogMessageText.Text = message;
        DialogPrimaryButton.Content = primary;
        DialogPrimaryButton.Background = (Brush)FindResource(isDestructive ? "DangerBrush" : "AccentBrush");
        DialogPrimaryButton.Foreground = isDestructive ? Brushes.White : (Brush)FindResource("InkBrush");
        DialogSecondaryButton.Content = secondary ?? "";
        DialogSecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        DialogCancelButton.Content = cancel;
        var hasCancelAction = secondary is not null || isDestructive || eyebrow == "NIMBO / ПЕРЕД ЗАПУСКОМ";
        DialogCancelButton.Visibility = hasCancelAction ? Visibility.Visible : Visibility.Collapsed;
        DialogEyebrowText.Foreground = (Brush)FindResource(isWarning || isDestructive ? "WarningBrush" : "SubtleBrush");

        _dialogCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        DialogOverlay.Visibility = Visibility.Visible;
        if (isDestructive || eyebrow == "NIMBO / ПЕРЕД ЗАПУСКОМ")
            DialogCancelButton.Focus();
        else
            DialogPrimaryButton.Focus();

        var result = await _dialogCompletion.Task;
        DialogOverlay.Visibility = Visibility.Collapsed;
        _dialogCompletion = null;
        return result;
    }

    private void CompleteDialog(string result) => _dialogCompletion?.TrySetResult(result);
    private void DialogPrimary_Click(object sender, RoutedEventArgs e) => CompleteDialog("primary");
    private void DialogSecondary_Click(object sender, RoutedEventArgs e) => CompleteDialog("secondary");
    private void DialogCancel_Click(object sender, RoutedEventArgs e) => CompleteDialog("cancel");
    private void DialogCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void DialogOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DialogOverlay) || e.OriginalSource is Grid)
            CompleteDialog("cancel");
    }

    private void AutoSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || ThresholdEnabledCheck is null || IntervalEnabledCheck is null)
            return;
        var wasActive = _settings.ThresholdEnabled || _settings.IntervalEnabled;
        _settings.ThresholdEnabled = ThresholdEnabledCheck.IsChecked == true;
        _settings.IntervalEnabled = IntervalEnabledCheck.IsChecked == true;
        if (!wasActive && (_settings.ThresholdEnabled || _settings.IntervalEnabled))
            _startedAt = DateTimeOffset.Now;
        SaveSettings();
    }

    private void StartupSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || RunAtStartupCheck is null || StartMinimizedCheck is null)
            return;
        _settings.RunAtStartup = RunAtStartupCheck.IsChecked == true;
        _settings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        StartMinimizedCheck.IsEnabled = _settings.RunAtStartup;
        try
        {
            ApplyStartupRegistration();
            _store.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _ = ShowDialogAsync("Не удалось изменить запуск", ex.Message, "Понятно", isWarning: true);
            _settings = _store.LoadSettings();
            _loadingSettings = true;
            LoadSettingsIntoControls();
            _loadingSettings = false;
        }
    }

    private void ApplyStartupRegistration()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null)
            throw new InvalidOperationException("Не удалось открыть список приложений автозапуска.");
        if (!_settings.RunAtStartup)
        {
            key.DeleteValue("Nimbo", throwOnMissingValue: false);
            return;
        }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к приложению.");
        var arguments = _settings.StartMinimized ? " --minimized" : "";
        key.SetValue("Nimbo", $"\"{executable}\"{arguments}");
    }

    private void IntervalMinus_Click(object sender, RoutedEventArgs e) => ChangeInterval(-5);
    private void IntervalPlus_Click(object sender, RoutedEventArgs e) => ChangeInterval(5);

    private void ChangeInterval(int delta)
    {
        _settings.IntervalMinutes = Math.Clamp(_settings.IntervalMinutes + delta, 10, 240);
        IntervalValueText.Text = _settings.IntervalMinutes.ToString();
        SaveSettings();
    }

    private void SaveSettings()
    {
        try { _store.SaveSettings(_settings); }
        catch (Exception ex) { AutoModeSummaryText.Text = $"Не удалось сохранить настройки: {ex.Message}"; }
        UpdateAutoSummary();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void InitializeTray()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Nimbo · монитор памяти",
            Icon = TrayIconFactory.Create(0),
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                if (IsVisible) Hide();
                else ShowFromTray();
            }
            else if (e.Button == Forms.MouseButtons.Middle)
            {
                ShowFromTray();
                _ = PromptCleanupAsync();
            }
            else if (e.Button == Forms.MouseButtons.Right)
            {
                ShowFromTray();
                SetPage("cleaning");
            }
        }));
    }

    private void UpdateTrayIcon(double percent)
    {
        if (_notifyIcon is null)
            return;
        var rounded = (int)Math.Round(percent);
        if (_lastTrayPercent == rounded)
            return;
        _lastTrayPercent = rounded;
        var previous = _notifyIcon.Icon;
        _notifyIcon.Icon = TrayIconFactory.Create(percent);
        _notifyIcon.Text = $"Nimbo · RAM {rounded}%";
        previous?.Dispose();
    }

    private void ShowFromTray()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void DisposeTray()
    {
        if (_notifyIcon is null)
            return;
        var icon = _notifyIcon.Icon;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        icon?.Dispose();
        _notifyIcon = null;
    }

    private void SetPage(string page)
    {
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        CleaningPage.Visibility = page == "cleaning" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        SetNav(OverviewNav, page == "overview");
        SetNav(CleanupNav, page == "cleaning");
        SetNav(HistoryNav, page == "history");
        SetNav(SettingsNav, page == "settings");
    }

    private static void SetNav(Button button, bool active)
    {
        button.Tag = active;
        button.Background = Brushes.Transparent;
        button.Foreground = active ? (Brush)Application.Current.FindResource("TextBrush") : (Brush)Application.Current.FindResource("MutedBrush");
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return index == 0 ? $"{value:0} {units[index]}" : $"{value:0.0} {units[index]}";
    }

    private static string Plural(int count, string one, string few, string many)
    {
        var n = count % 100;
        if (n is >= 11 and <= 14) return many;
        return (count % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    private void OverviewNav_Click(object sender, RoutedEventArgs e) => SetPage("overview");
    private void CleanupNav_Click(object sender, RoutedEventArgs e) => SetPage("cleaning");
    private void HistoryNav_Click(object sender, RoutedEventArgs e) => SetPage("history");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => SetPage("settings");
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
