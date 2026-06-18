using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        bool createdNew;
        using (Mutex mutex = new Mutex(true, @"Local\DeviljjuWorkTimeWidget", out createdNew))
        {
            if (!createdNew) return;
            new Application().Run(new WorkWidgetWindow());
        }
    }
}

internal sealed class Settings
{
    public double AnnualSalary { get; set; }
    public double WorkHoursPerDay { get; set; }
    public double WorkDaysPerYear { get; set; }
    public int WorkDaysYear { get; set; }
    public bool Topmost { get; set; }
    public bool AutoStart { get; set; }
    public bool EarningsCollapsed { get; set; }
    public string ThemeMode { get; set; }
    public string LunchStartTime { get; set; }
    public string LunchEndTime { get; set; }

    public static Settings Defaults()
    {
        int year = DateTime.Today.Year;
        return new Settings
        {
            AnnualSalary = 0,
            WorkHoursPerDay = 9.0,
            WorkDaysPerYear = CountWeekdays(year),
            WorkDaysYear = year,
            Topmost = true,
            AutoStart = true,
            EarningsCollapsed = true,
            ThemeMode = "system",
            LunchStartTime = "12:00",
            LunchEndTime = "13:00"
        };
    }

    public static int CountWeekdays(int year)
    {
        int days = 0;
        DateTime day = new DateTime(year, 1, 1);
        while (day.Year == year)
        {
            if (day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday)
            {
                days++;
            }
            day = day.AddDays(1);
        }
        return days;
    }
}

internal sealed class ThemePalette
{
    public string ShellBackground { get; set; }
    public string Border { get; set; }
    public string Line { get; set; }
    public string PrimaryText { get; set; }
    public string SecondaryText { get; set; }
    public string MutedText { get; set; }
    public string IncomeText { get; set; }
    public string Icon { get; set; }
    public string InputBackground { get; set; }
    public string ButtonBackground { get; set; }
    public string ButtonHover { get; set; }
    public string ButtonPressed { get; set; }
    public string SaveBackground { get; set; }
    public string SaveHover { get; set; }
    public string SavePressed { get; set; }
    public string SaveText { get; set; }
}

internal sealed class AttendanceRecord
{
    public DateTime LoginTime { get; set; }
    public DateTime ExpectedEndTime { get; set; }
    public string Path { get; set; }
    public string SystemStartTime { get; set; }
    public bool Created { get; set; }
}

internal sealed class WorkWidgetWindow : Window
{
    private readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
    private readonly string settingsPath;
    private readonly string attendanceRoot;
    private readonly string iconPath;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
    private readonly DispatcherTimer timer = new DispatcherTimer();
    private readonly System.Windows.Forms.NotifyIcon notifyIcon = new System.Windows.Forms.NotifyIcon();
    private readonly System.Windows.Forms.ContextMenuStrip trayMenu = new System.Windows.Forms.ContextMenuStrip();

    private Settings settings;
    private AttendanceRecord attendance;
    private DateTime detectedLoginTime;
    private DateTime loginTime;
    private DateTime expectedEndTime;
    private bool departureAlarmShown;
    private DateTime departureAlarmDate;
    private bool allowExit;

    private readonly List<FrameworkElement> themedElements = new List<FrameworkElement>();
    private Border shell;
    private TextBlock startTimeText;
    private TextBlock endTimeText;
    private TextBlock earnedText;
    private Grid earningsExpandedPanel;
    private Button earningsToggleButton;
    private Button settingsButton;
    private Button closeButton;
    private Button cancelButton;
    private Button saveButton;
    private Border settingsPanel;
    private TextBox startTimeBox;
    private TextBox salaryBox;
    private TextBox hoursBox;
    private TextBox daysBox;
    private TextBox lunchStartBox;
    private TextBox lunchEndBox;
    private CheckBox topmostBox;
    private CheckBox autoStartBox;
    private ComboBox themeBox;

    private const double ExpandedHeightValue = 188;
    private const double CollapsedHeightValue = 150;
    private const double SettingsHeightValue = 460;

    public WorkWidgetWindow()
    {
        settingsPath = Path.Combine(ResolveAppDir(), "settings.json");
        attendanceRoot = Path.Combine(baseDir, "attendance");
        iconPath = Path.Combine(baseDir, "assets", "work-widget.ico");

        settings = ReadSettings();
        EnsureWorkDaysForCurrentYear();
        ApplyAutoStartSetting();
        settings.EarningsCollapsed = true;
        detectedLoginTime = GetSessionLoginTime();
        attendance = GetAttendanceRecord(detectedLoginTime, settings.WorkHoursPerDay);
        loginTime = attendance.LoginTime;
        expectedEndTime = attendance.ExpectedEndTime;

        BuildUi();
        ApplyTheme();
        if (File.Exists(iconPath))
        {
            Icon = BitmapFrame.Create(new Uri(iconPath));
            notifyIcon.Icon = new System.Drawing.Icon(iconPath);
        }
        else
        {
            notifyIcon.Icon = System.Drawing.SystemIcons.Information;
        }
        notifyIcon.Text = "출근 퇴근 연봉 위젯";
        ConfigureTrayIcon();
        notifyIcon.Visible = true;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Closing += OnWindowClosing;
        Closed += delegate
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            trayMenu.Dispose();
        };

        ApplyEarningsVisibility();
        UpdateWidget();

        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += delegate { UpdateWidget(); };
        timer.Start();
    }

    private void BuildUi()
    {
        Width = 360;
        Height = ExpandedHeightValue;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = settings.Topmost;
        ShowInTaskbar = false;

        shell = Theme(new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true
        }, "Shell");
        Content = shell;

        Grid root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.Child = root;

        Grid title = new Grid { Background = Brushes.Transparent };
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        title.MouseLeftButtonDown += DragWindow;
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        title.Children.Add(Theme(new TextBlock
        {
            Text = "근무 위젯",
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        }, "PrimaryText"));

        settingsButton = IconButton(null, "설정");
        settingsButton.Content = OptionsIcon();
        settingsButton.Click += delegate { OpenSettings(); };
        Grid.SetColumn(settingsButton, 1);
        title.Children.Add(settingsButton);

        closeButton = IconButton(null, "트레이로 숨기기");
        closeButton.Content = CloseIcon();
        closeButton.Click += delegate { HideWidget(); };
        Grid.SetColumn(closeButton, 2);
        title.Children.Add(closeButton);

        AddLine(root, 1);

        Grid times = new Grid();
        times.ColumnDefinitions.Add(new ColumnDefinition());
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        times.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(times, 2);
        root.Children.Add(times);

        AddTimeColumn(times, "출근 시간", out startTimeText, 0);
        Border divider = Theme(new Border { Margin = new Thickness(0, 12, 0, 12) }, "Line");
        Grid.SetColumn(divider, 1);
        times.Children.Add(divider);
        AddTimeColumn(times, "예상 퇴근 시간", out endTimeText, 2);

        AddLine(root, 3);

        Grid earnings = new Grid();
        Grid.SetRow(earnings, 4);
        root.Children.Add(earnings);

        earningsExpandedPanel = new Grid();
        earnings.Children.Add(earningsExpandedPanel);
        earnedText = new TextBlock
        {
            Margin = new Thickness(18, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 24,
            FontWeight = FontWeights.Bold
        };
        Theme(earnedText, "IncomeText");
        earningsExpandedPanel.Children.Add(earnedText);
        earningsToggleButton = IconButton(null, "수입 접기/펼치기");
        earningsToggleButton.Width = 32;
        earningsToggleButton.Height = 32;
        earningsToggleButton.HorizontalAlignment = HorizontalAlignment.Right;
        earningsToggleButton.VerticalAlignment = VerticalAlignment.Top;
        earningsToggleButton.Margin = new Thickness(0, 4, 14, 4);
        earningsToggleButton.Click += delegate
        {
            settings.EarningsCollapsed = !settings.EarningsCollapsed;
            SaveSettings();
            ApplyEarningsVisibility();
        };
        Panel.SetZIndex(earningsToggleButton, 10);
        earnings.Children.Add(earningsToggleButton);

        BuildSettingsPanel(root);
    }

    private void BuildSettingsPanel(Grid root)
    {
        settingsPanel = Theme(new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        }, "Shell");
        Grid.SetRowSpan(settingsPanel, 5);
        Panel.SetZIndex(settingsPanel, 20);
        root.Children.Add(settingsPanel);

        Grid grid = new Grid { Margin = new Thickness(18) };
        settingsPanel.Child = grid;
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

        Grid header = new Grid { Background = Brushes.Transparent };
        header.MouseLeftButtonDown += DragWindow;
        grid.Children.Add(header);
        header.Children.Add(Theme(new TextBlock
        {
            Text = "설정",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        }, "PrimaryText"));

        startTimeBox = AddSettingRow(grid, "출근시간", 1);
        salaryBox = AddSettingRow(grid, "연봉", 2);
        hoursBox = AddSettingRow(grid, "하루 근무시간", 3);
        daysBox = AddSettingRow(grid, "연 근무일수", 4);
        lunchStartBox = AddSettingRow(grid, "점심 시작", 5);
        lunchEndBox = AddSettingRow(grid, "점심 종료", 6);
        themeBox = AddThemeRow(grid, 7);

        topmostBox = new CheckBox
        {
            Content = "항상 위에 표시",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Theme(topmostBox, "SecondaryText");
        Grid.SetRow(topmostBox, 8);
        grid.Children.Add(topmostBox);

        autoStartBox = new CheckBox
        {
            Content = "Windows 시작 시 실행",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Theme(autoStartBox, "SecondaryText");
        Grid.SetRow(autoStartBox, 9);
        grid.Children.Add(autoStartBox);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 10);
        grid.Children.Add(buttons);
        cancelButton = new Button { Content = "취소", Width = 72, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
        cancelButton.Click += delegate
        {
            settingsPanel.Visibility = Visibility.Collapsed;
            ApplyEarningsVisibility();
        };
        buttons.Children.Add(cancelButton);

        saveButton = new Button
        {
            Content = "저장",
            Width = 72,
            Cursor = Cursors.Hand
        };
        saveButton.Click += delegate { SaveSettingsFromUi(); };
        buttons.Children.Add(saveButton);
    }

    private TextBox AddSettingRow(Grid grid, string label, int row)
    {
        TextBlock text = Theme(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        }, "SecondaryText");
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        TextBox box = Theme(new TextBox
        {
            Margin = new Thickness(108, 3, 0, 3),
            Padding = new Thickness(8, 4, 8, 4)
        }, "Input");
        Grid.SetRow(box, row);
        grid.Children.Add(box);
        return box;
    }

    private ComboBox AddThemeRow(Grid grid, int row)
    {
        TextBlock text = Theme(new TextBlock
        {
            Text = "화면 모드",
            VerticalAlignment = VerticalAlignment.Center
        }, "SecondaryText");
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        ComboBox box = Theme(new ComboBox
        {
            Margin = new Thickness(108, 3, 0, 3),
            Padding = new Thickness(6, 2, 6, 2),
            Cursor = Cursors.Hand
        }, "Input");
        box.Items.Add(new ComboBoxItem { Content = "시스템", Tag = "system" });
        box.Items.Add(new ComboBoxItem { Content = "라이트", Tag = "light" });
        box.Items.Add(new ComboBoxItem { Content = "다크", Tag = "dark" });
        Grid.SetRow(box, row);
        grid.Children.Add(box);
        return box;
    }

    private void AddTimeColumn(Grid parent, string caption, out TextBlock value, int column)
    {
        StackPanel panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(panel, column);
        parent.Children.Add(panel);
        panel.Children.Add(Theme(new TextBlock
        {
            Text = caption,
            TextAlignment = TextAlignment.Center,
            FontSize = 12
        }, "MutedText"));
        value = Theme(new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        }, "PrimaryText");
        panel.Children.Add(value);
    }

    private void AddLine(Grid root, int row)
    {
        Border line = Theme(new Border(), "Line");
        Grid.SetRow(line, row);
        root.Children.Add(line);
    }

    private Button IconButton(string content, string tooltip)
    {
        Button button = new Button
        {
            Content = content,
            FontSize = 16,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        Theme(button, "IconButton");
        button.Padding = new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Template = CreateIconButtonTemplate();
        return button;
    }

    private ControlTemplate CreateIconButtonTemplate()
    {
        ThemePalette palette = CurrentPalette();
        FrameworkElementFactory grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(Grid.BackgroundProperty, Brushes.Transparent);

        FrameworkElementFactory hover = new FrameworkElementFactory(typeof(Border));
        hover.Name = "HoverBackground";
        hover.SetValue(Border.WidthProperty, 28.0);
        hover.SetValue(Border.HeightProperty, 28.0);
        hover.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        hover.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        hover.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        hover.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(hover);

        FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        grid.AppendChild(presenter);

        ControlTemplate template = new ControlTemplate(typeof(Button));
        template.VisualTree = grid;

        Trigger over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, BrushFrom(palette.ButtonHover), "HoverBackground"));
        template.Triggers.Add(over);

        Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, BrushFrom(palette.ButtonPressed), "HoverBackground"));
        template.Triggers.Add(pressed);

        return template;
    }

    private UIElement ChevronIcon(bool up)
    {
        System.Windows.Shapes.Path path = new System.Windows.Shapes.Path
        {
            Width = 10,
            Height = 6,
            Stretch = Stretch.Uniform,
            Stroke = BrushFrom(CurrentPalette().PrimaryText),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse(up ? "M 1 5 L 5 1 L 9 5" : "M 1 1 L 5 5 L 9 1")
        };
        return path;
    }

    private UIElement CloseIcon()
    {
        Grid grid = new Grid { Width = 10, Height = 10 };
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Stroke = BrushFrom(CurrentPalette().MutedText),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 1 1 L 9 9")
        });
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Stroke = BrushFrom(CurrentPalette().MutedText),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 9 1 L 1 9")
        });
        return grid;
    }

    private UIElement OptionsIcon()
    {
        Canvas grid = new Canvas { Width = 16, Height = 16 };
        ThemePalette palette = CurrentPalette();
        Brush fill = BrushFrom(palette.Icon);

        grid.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 6.8,
            Height = 6.8,
            Fill = fill
        });
        Canvas.SetLeft(grid.Children[0], 4.6);
        Canvas.SetTop(grid.Children[0], 4.6);

        grid.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 3.4,
            Height = 3.4,
            Fill = BrushFrom(palette.ShellBackground)
        });
        Canvas.SetLeft(grid.Children[1], 6.3);
        Canvas.SetTop(grid.Children[1], 6.3);

        foreach (double angle in new[] { 0.0, 60.0, 120.0, 180.0, 240.0, 300.0 })
        {
            Border tooth = new Border
            {
                Width = 2.4,
                Height = 4.6,
                Background = fill,
                CornerRadius = new CornerRadius(1.2),
                RenderTransformOrigin = new Point(0.5, 1.28),
                RenderTransform = new RotateTransform(angle)
            };
            Canvas.SetLeft(tooth, 6.8);
            Canvas.SetTop(tooth, 0.8);
            grid.Children.Add(tooth);
        }

        return grid;
    }

    private ControlTemplate CreateTextButtonTemplate(string normal, string hover, string pressed, string border, string foreground)
    {
        FrameworkElementFactory root = new FrameworkElementFactory(typeof(Border));
        root.Name = "ButtonBorder";
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        root.SetValue(Border.BackgroundProperty, BrushFrom(normal));
        root.SetValue(Border.BorderBrushProperty, BrushFrom(border));
        root.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetValue(TextElement.ForegroundProperty, BrushFrom(foreground));
        root.AppendChild(presenter);

        ControlTemplate template = new ControlTemplate(typeof(Button));
        template.VisualTree = root;

        Trigger over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, BrushFrom(hover), "ButtonBorder"));
        template.Triggers.Add(over);

        Trigger down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        down.Setters.Add(new Setter(Border.BackgroundProperty, BrushFrom(pressed), "ButtonBorder"));
        template.Triggers.Add(down);

        return template;
    }

    private void ApplyEarningsVisibility()
    {
        if (settings.EarningsCollapsed)
        {
            earningsExpandedPanel.Visibility = Visibility.Collapsed;
            earningsToggleButton.Content = ChevronIcon(true);
            if (settingsPanel.Visibility != Visibility.Visible) Height = CollapsedHeightValue;
        }
        else
        {
            earningsExpandedPanel.Visibility = Visibility.Visible;
            earningsToggleButton.Content = ChevronIcon(false);
            if (settingsPanel.Visibility != Visibility.Visible) Height = ExpandedHeightValue;
        }
    }

    private void UpdateWidget()
    {
        EnsureWorkDaysForCurrentYear();
        double hoursPerDay = Math.Max(0.1, settings.WorkHoursPerDay);
        double paidHoursPerDay = CalculatePaidHoursPerDay(hoursPerDay);
        double daysPerYear = Math.Max(1, settings.WorkDaysPerYear);
        double salary = Math.Max(0, settings.AnnualSalary);
        double paidSeconds = CalculatePaidSeconds(loginTime, DateTime.Now);
        double earned = paidSeconds * salary / (daysPerYear * paidHoursPerDay * 3600);

        startTimeText.Text = loginTime.ToString("HH:mm");
        endTimeText.Text = expectedEndTime.ToString("HH:mm");
        earnedText.Text = "+ " + FormatWon(earned);
        Topmost = settings.Topmost;
        CheckDepartureAlarm(DateTime.Now);
    }

    private void CheckDepartureAlarm(DateTime now)
    {
        if (departureAlarmDate != expectedEndTime.Date)
        {
            departureAlarmDate = expectedEndTime.Date;
            departureAlarmShown = false;
        }

        DateTime alarmTime = expectedEndTime.AddMinutes(-5);
        if (departureAlarmShown || now < alarmTime || now >= expectedEndTime)
        {
            return;
        }

        departureAlarmShown = true;
        notifyIcon.ShowBalloonTip(
            8000,
            "퇴근 5분 전",
            "예상 퇴근시간은 " + expectedEndTime.ToString("HH:mm") + "입니다.",
            System.Windows.Forms.ToolTipIcon.None
        );
    }

    private void SyncSettingsFields()
    {
        startTimeBox.Text = loginTime.ToString("HH:mm");
        salaryBox.Text = settings.AnnualSalary.ToString("N0", CultureInfo.GetCultureInfo("ko-KR"));
        hoursBox.Text = settings.WorkHoursPerDay.ToString("0.##", CultureInfo.InvariantCulture);
        daysBox.Text = settings.WorkDaysPerYear.ToString("0", CultureInfo.InvariantCulture);
        lunchStartBox.Text = settings.LunchStartTime;
        lunchEndBox.Text = settings.LunchEndTime;
        topmostBox.IsChecked = settings.Topmost;
        autoStartBox.IsChecked = settings.AutoStart;
        SelectTheme(settings.ThemeMode);
    }

    private void SaveSettingsFromUi()
    {
        settings.AnnualSalary = ParseDouble(salaryBox.Text, 0);
        settings.WorkHoursPerDay = Math.Max(0.1, ParseDouble(hoursBox.Text, 9));
        settings.WorkDaysPerYear = Math.Max(1, ParseDouble(daysBox.Text, 260));
        settings.WorkDaysYear = DateTime.Today.Year;
        settings.Topmost = topmostBox.IsChecked == true;
        settings.AutoStart = autoStartBox.IsChecked == true;
        settings.ThemeMode = SelectedThemeMode();
        settings.LunchStartTime = NormalizeTimeText(lunchStartBox.Text, settings.LunchStartTime);
        settings.LunchEndTime = NormalizeTimeText(lunchEndBox.Text, settings.LunchEndTime);

        loginTime = ParseTimeOnToday(startTimeBox.Text, loginTime);
        expectedEndTime = loginTime.AddHours(settings.WorkHoursPerDay);
        departureAlarmShown = false;
        departureAlarmDate = expectedEndTime.Date;
        string systemStart = string.IsNullOrWhiteSpace(attendance.SystemStartTime)
            ? detectedLoginTime.ToString("HH:mm")
            : attendance.SystemStartTime;
        attendance.Path = SaveTodayAttendance(loginTime, expectedEndTime, systemStart);
        attendance.SystemStartTime = systemStart;

        SaveSettings();
        ApplyAutoStartSetting();
        ApplyTheme();
        settingsPanel.Visibility = Visibility.Collapsed;
        ApplyEarningsVisibility();
        UpdateWidget();
    }

    private double CalculatePaidSeconds(DateTime start, DateTime end)
    {
        double elapsed = Math.Max(0, (end - start).TotalSeconds);
        DateTime lunchStart;
        DateTime lunchEnd;
        if (!TryParseTimeOnDate(settings.LunchStartTime, start.Date, out lunchStart) ||
            !TryParseTimeOnDate(settings.LunchEndTime, start.Date, out lunchEnd))
        {
            return elapsed;
        }

        if (lunchEnd <= lunchStart) lunchEnd = lunchEnd.AddDays(1);

        DateTime overlapStart = start > lunchStart ? start : lunchStart;
        DateTime overlapEnd = end < lunchEnd ? end : lunchEnd;
        double lunchSeconds = Math.Max(0, (overlapEnd - overlapStart).TotalSeconds);
        return Math.Max(0, elapsed - lunchSeconds);
    }

    private double CalculatePaidHoursPerDay(double hoursPerDay)
    {
        double lunchHours = CalculateLunchDurationHours(DateTime.Today);
        return Math.Max(0.1, hoursPerDay - lunchHours);
    }

    private double CalculateLunchDurationHours(DateTime date)
    {
        DateTime lunchStart;
        DateTime lunchEnd;
        if (!TryParseTimeOnDate(settings.LunchStartTime, date, out lunchStart) ||
            !TryParseTimeOnDate(settings.LunchEndTime, date, out lunchEnd))
        {
            return 0;
        }

        if (lunchEnd <= lunchStart) lunchEnd = lunchEnd.AddDays(1);
        return Math.Max(0, (lunchEnd - lunchStart).TotalHours);
    }

    private T Theme<T>(T element, string role) where T : FrameworkElement
    {
        element.Tag = role;
        themedElements.Add(element);
        return element;
    }

    private void ApplyTheme()
    {
        ThemePalette palette = CurrentPalette();
        foreach (FrameworkElement element in themedElements)
        {
            string role = element.Tag as string;
            TextBlock text = element as TextBlock;
            Border border = element as Border;
            Control control = element as Control;

            if (role == "Shell" && border != null)
            {
                border.Background = BrushFrom(palette.ShellBackground);
                border.BorderBrush = BrushFrom(palette.Border);
            }
            else if (role == "Line" && border != null)
            {
                border.Background = BrushFrom(palette.Line);
            }
            else if (role == "PrimaryText" && text != null)
            {
                text.Foreground = BrushFrom(palette.PrimaryText);
            }
            else if (role == "SecondaryText" && control != null)
            {
                control.Foreground = BrushFrom(palette.SecondaryText);
            }
            else if (role == "SecondaryText" && text != null)
            {
                text.Foreground = BrushFrom(palette.SecondaryText);
            }
            else if (role == "MutedText" && text != null)
            {
                text.Foreground = BrushFrom(palette.MutedText);
            }
            else if (role == "IncomeText" && text != null)
            {
                text.Foreground = BrushFrom(palette.IncomeText);
            }
            else if (role == "Input" && control != null)
            {
                control.Background = BrushFrom(palette.InputBackground);
                control.Foreground = BrushFrom(palette.PrimaryText);
                control.BorderBrush = BrushFrom(palette.Border);
                ComboBox comboBox = control as ComboBox;
                if (comboBox != null)
                {
                    comboBox.Template = CreateComboBoxTemplate(palette);
                    comboBox.ItemContainerStyle = CreateComboBoxItemStyle(palette);
                }
            }
            else if (role == "IconButton" && control != null)
            {
                control.Foreground = BrushFrom(palette.PrimaryText);
                control.Template = CreateIconButtonTemplate();
            }
        }

        if (settingsButton != null) settingsButton.Content = OptionsIcon();
        if (closeButton != null) closeButton.Content = CloseIcon();
        if (earningsToggleButton != null) earningsToggleButton.Content = ChevronIcon(settings.EarningsCollapsed);
        if (cancelButton != null) cancelButton.Template = CreateTextButtonTemplate(palette.ShellBackground, palette.ButtonHover, palette.ButtonPressed, palette.Border, palette.SecondaryText);
        if (saveButton != null) saveButton.Template = CreateTextButtonTemplate(palette.SaveBackground, palette.SaveHover, palette.SavePressed, palette.SaveBackground, palette.SaveText);
    }

    private ControlTemplate CreateComboBoxTemplate(ThemePalette palette)
    {
        FrameworkElementFactory root = new FrameworkElementFactory(typeof(Grid));

        FrameworkElementFactory toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.Name = "DropDownToggle";
        toggle.SetValue(ToggleButton.FocusableProperty, false);
        toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
        toggle.SetValue(ToggleButton.BackgroundProperty, BrushFrom(palette.InputBackground));
        toggle.SetValue(ToggleButton.BorderBrushProperty, BrushFrom(palette.Border));
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen") { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
        toggle.SetValue(ToggleButton.TemplateProperty, CreateComboBoxToggleTemplate(palette));
        root.AppendChild(toggle);

        FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 28, 0));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(TextElement.ForegroundProperty, BrushFrom(palette.PrimaryText));
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("SelectionBoxItem") { RelativeSource = RelativeSource.TemplatedParent });
        root.AppendChild(presenter);

        FrameworkElementFactory popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen") { RelativeSource = RelativeSource.TemplatedParent });

        FrameworkElementFactory dropdownBorder = new FrameworkElementFactory(typeof(Border));
        dropdownBorder.SetValue(Border.BackgroundProperty, BrushFrom(palette.InputBackground));
        dropdownBorder.SetValue(Border.BorderBrushProperty, BrushFrom(palette.Border));
        dropdownBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        dropdownBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        dropdownBorder.SetBinding(FrameworkElement.MinWidthProperty, new Binding("ActualWidth") { RelativeSource = RelativeSource.TemplatedParent });

        FrameworkElementFactory scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
        scrollViewer.SetValue(ScrollViewer.CanContentScrollProperty, true);
        FrameworkElementFactory itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        scrollViewer.AppendChild(itemsPresenter);
        dropdownBorder.AppendChild(scrollViewer);
        popup.AppendChild(dropdownBorder);
        root.AppendChild(popup);

        ControlTemplate template = new ControlTemplate(typeof(ComboBox));
        template.VisualTree = root;
        return template;
    }

    private ControlTemplate CreateComboBoxToggleTemplate(ThemePalette palette)
    {
        FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ToggleBorder";
        border.SetValue(Border.BackgroundProperty, BrushFrom(palette.InputBackground));
        border.SetValue(Border.BorderBrushProperty, BrushFrom(palette.Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));

        FrameworkElementFactory grid = new FrameworkElementFactory(typeof(Grid));
        FrameworkElementFactory arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        arrow.SetValue(System.Windows.Shapes.Path.WidthProperty, 7.0);
        arrow.SetValue(System.Windows.Shapes.Path.HeightProperty, 4.0);
        arrow.SetValue(System.Windows.Shapes.Path.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(System.Windows.Shapes.Path.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(System.Windows.Shapes.Path.MarginProperty, new Thickness(0, 0, 9, 0));
        arrow.SetValue(System.Windows.Shapes.Path.FillProperty, BrushFrom(palette.MutedText));
        arrow.SetValue(System.Windows.Shapes.Path.StretchProperty, Stretch.Uniform);
        arrow.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0 0 L 3.5 4 L 7 0 Z"));
        grid.AppendChild(arrow);
        border.AppendChild(grid);

        ControlTemplate template = new ControlTemplate(typeof(ToggleButton));
        template.VisualTree = border;

        Trigger over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, BrushFrom(palette.ButtonHover), "ToggleBorder"));
        template.Triggers.Add(over);

        return template;
    }

    private Style CreateComboBoxItemStyle(ThemePalette palette)
    {
        Style style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, BrushFrom(palette.InputBackground)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, BrushFrom(palette.PrimaryText)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));

        Trigger highlighted = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        highlighted.Setters.Add(new Setter(Control.BackgroundProperty, BrushFrom(palette.ButtonHover)));
        highlighted.Setters.Add(new Setter(Control.ForegroundProperty, BrushFrom(palette.PrimaryText)));
        style.Triggers.Add(highlighted);

        Trigger selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, BrushFrom(palette.ButtonPressed)));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, BrushFrom(palette.PrimaryText)));
        style.Triggers.Add(selected);

        return style;
    }

    private ThemePalette CurrentPalette()
    {
        bool dark = IsDarkTheme();
        if (dark)
        {
            return new ThemePalette
            {
                ShellBackground = "#111827",
                Border = "#374151",
                Line = "#374151",
                PrimaryText = "#F8FAFC",
                SecondaryText = "#CBD5E1",
                MutedText = "#94A3B8",
                IncomeText = "#4ADE80",
                Icon = "#B6BBCB",
                InputBackground = "#1F2937",
                ButtonBackground = "#111827",
                ButtonHover = "#253244",
                ButtonPressed = "#334155",
                SaveBackground = "#F8FAFC",
                SaveHover = "#E2E8F0",
                SavePressed = "#CBD5E1",
                SaveText = "#0F172A"
            };
        }

        return new ThemePalette
        {
            ShellBackground = "#F8FAFC",
            Border = "#C4CCD6",
            Line = "#CBD5E1",
            PrimaryText = "#0F172A",
            SecondaryText = "#334155",
            MutedText = "#64748B",
            IncomeText = "#15803D",
            Icon = "#B6BBCB",
            InputBackground = "#FFFFFF",
            ButtonBackground = "#F8FAFC",
            ButtonHover = "#E2E8F0",
            ButtonPressed = "#CBD5E1",
            SaveBackground = "#0F172A",
            SaveHover = "#1E293B",
            SavePressed = "#334155",
            SaveText = "#FFFFFF"
        };
    }

    private bool IsDarkTheme()
    {
        string mode = NormalizeThemeMode(settings == null ? null : settings.ThemeMode);
        if (mode == "dark") return true;
        if (mode == "light") return false;

        try
        {
            object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0;
        }
        catch
        {
            return false;
        }
    }

    private string NormalizeThemeMode(string mode)
    {
        mode = (mode ?? "").Trim().ToLowerInvariant();
        return mode == "light" || mode == "dark" ? mode : "system";
    }

    private void SelectTheme(string mode)
    {
        mode = NormalizeThemeMode(mode);
        foreach (object item in themeBox.Items)
        {
            ComboBoxItem comboItem = item as ComboBoxItem;
            if (comboItem != null && (comboItem.Tag as string) == mode)
            {
                themeBox.SelectedItem = comboItem;
                return;
            }
        }
        themeBox.SelectedIndex = 0;
    }

    private string SelectedThemeMode()
    {
        ComboBoxItem item = themeBox.SelectedItem as ComboBoxItem;
        return NormalizeThemeMode(item == null ? null : item.Tag as string);
    }

    private Settings ReadSettings()
    {
        Settings defaults = Settings.Defaults();
        if (!File.Exists(settingsPath))
        {
            File.WriteAllText(settingsPath, serializer.Serialize(defaults), new UTF8Encoding(true));
            return defaults;
        }

        try
        {
            string json = File.ReadAllText(settingsPath, Encoding.UTF8);
            Settings loaded = serializer.Deserialize<Settings>(json);
            if (loaded == null) return defaults;
            Dictionary<string, object> raw = serializer.Deserialize<Dictionary<string, object>>(json);
            if (raw == null || !raw.ContainsKey("AutoStart")) loaded.AutoStart = defaults.AutoStart;
            if (string.IsNullOrWhiteSpace(loaded.ThemeMode)) loaded.ThemeMode = defaults.ThemeMode;
            if (loaded.WorkDaysYear <= 0) loaded.WorkDaysYear = DateTime.Today.Year;
            if (loaded.WorkDaysPerYear <= 0) loaded.WorkDaysPerYear = defaults.WorkDaysPerYear;
            loaded.LunchStartTime = NormalizeTimeText(loaded.LunchStartTime, defaults.LunchStartTime);
            loaded.LunchEndTime = NormalizeTimeText(loaded.LunchEndTime, defaults.LunchEndTime);
            return loaded;
        }
        catch
        {
            File.WriteAllText(settingsPath, serializer.Serialize(defaults), new UTF8Encoding(true));
            return defaults;
        }
    }

    private void SaveSettings()
    {
        File.WriteAllText(settingsPath, serializer.Serialize(settings), new UTF8Encoding(true));
    }

    private void ApplyAutoStartSetting()
    {
        string shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Work Time Widget.lnk");
        try
        {
            if (settings.AutoStart)
            {
                CreateStartupShortcut(shortcutPath);
            }
            else if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }
        }
        catch { }
    }

    private void CreateStartupShortcut(string shortcutPath)
    {
        string executable = Process.GetCurrentProcess().MainModule.FileName;
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        object shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { executable });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(executable) });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { executable + ",0" });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "출근 퇴근 연봉 위젯" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private void ConfigureTrayIcon()
    {
        trayMenu.ShowImageMargin = false;

        System.Windows.Forms.ToolStripMenuItem openItem = new System.Windows.Forms.ToolStripMenuItem("위젯 열기");
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += delegate { Dispatcher.BeginInvoke(new Action(delegate { ShowWidget(); })); };
        trayMenu.Items.Add(openItem);

        System.Windows.Forms.ToolStripMenuItem settingsItem = new System.Windows.Forms.ToolStripMenuItem("설정");
        settingsItem.Click += delegate { Dispatcher.BeginInvoke(new Action(delegate { OpenSettings(); })); };
        trayMenu.Items.Add(settingsItem);
        trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        System.Windows.Forms.ToolStripMenuItem exitItem = new System.Windows.Forms.ToolStripMenuItem("종료");
        exitItem.Click += delegate { Dispatcher.BeginInvoke(new Action(delegate { ExitApplication(); })); };
        trayMenu.Items.Add(exitItem);

        notifyIcon.ContextMenuStrip = trayMenu;
        notifyIcon.MouseClick += delegate(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(new Action(delegate { ShowWidget(); }));
            }
        };
        notifyIcon.BalloonTipClicked += delegate { Dispatcher.BeginInvoke(new Action(delegate { ShowWidget(); })); };
    }

    private void ShowWidget()
    {
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = settings.Topmost;
    }

    private void OpenSettings()
    {
        ShowWidget();
        SyncSettingsFields();
        Height = SettingsHeightValue;
        settingsPanel.Visibility = Visibility.Visible;
    }

    private void HideWidget()
    {
        Hide();
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (allowExit) return;
        e.Cancel = true;
        HideWidget();
    }

    private void ExitApplication()
    {
        allowExit = true;
        timer.Stop();
        Close();
        Application.Current.Shutdown();
    }

    private void EnsureWorkDaysForCurrentYear()
    {
        int currentYear = DateTime.Today.Year;
        if (settings.WorkDaysYear == currentYear)
        {
            return;
        }

        settings.WorkDaysYear = currentYear;
        settings.WorkDaysPerYear = Settings.CountWeekdays(currentYear);
        SaveSettings();

        if (daysBox != null && settingsPanel != null && settingsPanel.Visibility == Visibility.Visible)
        {
            daysBox.Text = settings.WorkDaysPerYear.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    private string ResolveAppDir()
    {
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkTimeWidget");
        string fallback = Path.Combine(baseDir, ".work-widget");
        foreach (string dir in new[] { appData, fallback })
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return dir;
            }
            catch { }
        }
        throw new InvalidOperationException("설정 파일을 저장할 수 있는 폴더를 찾지 못했습니다.");
    }

    private AttendanceRecord GetAttendanceRecord(DateTime detected, double workHours)
    {
        DateTime today = DateTime.Today;
        string dateText = today.ToString("yyyy-MM-dd");
        string monthPath = Path.Combine(attendanceRoot, today.ToString("yyyy"), today.ToString("MM") + ".csv");
        List<Dictionary<string, string>> rows = ReadAttendanceRows(monthPath);
        Dictionary<string, string> existing = rows.FirstOrDefault(row => GetValue(row, "날짜") == dateText);

        if (existing != null)
        {
            DateTime start = DateTime.ParseExact(dateText + " " + GetValue(existing, "출근시간"), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            DateTime end = DateTime.ParseExact(dateText + " " + GetValue(existing, "예상퇴근시간"), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (end < start) end = end.AddDays(1);
            string systemStart = GetValue(existing, "시스템시작시간");
            if (string.IsNullOrWhiteSpace(systemStart)) systemStart = detected.ToString("HH:mm");
            existing["시스템시작시간"] = systemStart;
            WriteAttendanceRows(monthPath, rows);
            return new AttendanceRecord { LoginTime = start, ExpectedEndTime = end, Path = monthPath, SystemStartTime = systemStart, Created = false };
        }

        DateTime login = new DateTime(detected.Year, detected.Month, detected.Day, detected.Hour, detected.Minute, 0);
        DateTime expected = login.AddHours(Math.Max(0.1, workHours));
        string sys = login.ToString("HH:mm");
        rows.RemoveAll(row => GetValue(row, "날짜") == dateText);
        rows.Add(new Dictionary<string, string>
        {
            { "날짜", dateText },
            { "출근시간", login.ToString("HH:mm") },
            { "예상퇴근시간", expected.ToString("HH:mm") },
            { "시스템시작시간", sys }
        });
        WriteAttendanceRows(monthPath, rows);
        return new AttendanceRecord { LoginTime = login, ExpectedEndTime = expected, Path = monthPath, SystemStartTime = sys, Created = true };
    }

    private string SaveTodayAttendance(DateTime start, DateTime end, string systemStart)
    {
        DateTime today = DateTime.Today;
        string dateText = today.ToString("yyyy-MM-dd");
        string monthPath = Path.Combine(attendanceRoot, today.ToString("yyyy"), today.ToString("MM") + ".csv");
        List<Dictionary<string, string>> rows = ReadAttendanceRows(monthPath);
        rows.RemoveAll(row => GetValue(row, "날짜") == dateText);
        rows.Add(new Dictionary<string, string>
        {
            { "날짜", dateText },
            { "출근시간", start.ToString("HH:mm") },
            { "예상퇴근시간", end.ToString("HH:mm") },
            { "시스템시작시간", string.IsNullOrWhiteSpace(systemStart) ? start.ToString("HH:mm") : systemStart }
        });
        WriteAttendanceRows(monthPath, rows);
        return monthPath;
    }

    private List<Dictionary<string, string>> ReadAttendanceRows(string path)
    {
        List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return rows;
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length < 2) return rows;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            string[] parts = lines[i].Split(new[] { ',' }, 3);
            Dictionary<string, string> row = new Dictionary<string, string>();
            row["날짜"] = parts.Length > 0 ? parts[0].Trim() : "";
            row["출근시간"] = parts.Length > 1 ? parts[1].Trim() : "";
            string third = parts.Length > 2 ? parts[2] : "";
            string[] endParts = third.Split(new[] { ';' }, 2);
            row["예상퇴근시간"] = endParts.Length > 0 ? endParts[0].Trim() : "";
            row["시스템시작시간"] = endParts.Length > 1 ? endParts[1].Trim() : "";
            rows.Add(row);
        }
        return rows;
    }

    private void WriteAttendanceRows(string path, List<Dictionary<string, string>> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        List<string> lines = new List<string> { "날짜,출근시간,예상퇴근시간;시스템시작시간" };
        foreach (Dictionary<string, string> row in rows.OrderBy(row => GetValue(row, "날짜")))
        {
            lines.Add(string.Format("{0},{1},{2}; {3}", GetValue(row, "날짜"), GetValue(row, "출근시간"), GetValue(row, "예상퇴근시간"), GetValue(row, "시스템시작시간")));
        }
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(true));
    }

    private DateTime GetSessionLoginTime()
    {
        string quser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "quser.exe");
        if (File.Exists(quser))
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(quser) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(1000);
                    foreach (string raw in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = raw.TrimStart('>', ' ');
                        if (!line.Contains(Environment.UserName)) continue;
                        string[] parts = line.Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries);
                        DateTime parsed;
                        if (parts.Length > 0 && DateTime.TryParse(parts[parts.Length - 1], CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
                            return parsed;
                    }
                }
            }
            catch { }
        }

        try
        {
            Process explorer = Process.GetProcessesByName("explorer").OrderBy(p => p.StartTime).FirstOrDefault();
            if (explorer != null) return explorer.StartTime;
        }
        catch { }

        return DateTime.Now;
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    private static Brush BrushFrom(string color)
    {
        return (Brush)new BrushConverter().ConvertFromString(color);
    }

    private static string GetValue(Dictionary<string, string> row, string key)
    {
        string value;
        return row != null && row.TryGetValue(key, out value) ? value : "";
    }

    private static double ParseDouble(string text, double fallback)
    {
        double value;
        string clean = new string((text ?? "").Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
    }

    private static DateTime ParseTimeOnToday(string text, DateTime fallback)
    {
        DateTime parsed;
        foreach (string format in new[] { "H:mm", "HH:mm" })
        {
            if (DateTime.TryParseExact((text ?? "").Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return DateTime.Today.AddHours(parsed.Hour).AddMinutes(parsed.Minute);
        }
        return DateTime.Today.AddHours(fallback.Hour).AddMinutes(fallback.Minute);
    }

    private static bool TryParseTimeOnDate(string text, DateTime date, out DateTime value)
    {
        DateTime parsed;
        foreach (string format in new[] { "H:mm", "HH:mm" })
        {
            if (DateTime.TryParseExact((text ?? "").Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                value = date.AddHours(parsed.Hour).AddMinutes(parsed.Minute);
                return true;
            }
        }

        value = date;
        return false;
    }

    private static string NormalizeTimeText(string text, string fallback)
    {
        DateTime parsed;
        if (TryParseTimeOnDate(text, DateTime.Today, out parsed))
        {
            return parsed.ToString("HH:mm");
        }

        if (TryParseTimeOnDate(fallback, DateTime.Today, out parsed))
        {
            return parsed.ToString("HH:mm");
        }

        return "12:00";
    }

    private static string FormatWon(double value)
    {
        return "₩ " + Math.Floor(Math.Max(0, value)).ToString("N0", CultureInfo.GetCultureInfo("ko-KR"));
    }
}
