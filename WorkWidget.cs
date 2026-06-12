using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        new Application().Run(new WorkWidgetWindow());
    }
}

internal sealed class Settings
{
    public double AnnualSalary { get; set; }
    public double WorkHoursPerDay { get; set; }
    public double WorkDaysPerYear { get; set; }
    public bool Topmost { get; set; }
    public bool EarningsCollapsed { get; set; }

    public static Settings Defaults()
    {
        return new Settings
        {
            AnnualSalary = 0,
            WorkHoursPerDay = 9.0,
            WorkDaysPerYear = 260,
            Topmost = true,
            EarningsCollapsed = true
        };
    }
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

    private Settings settings;
    private AttendanceRecord attendance;
    private DateTime detectedLoginTime;
    private DateTime loginTime;
    private DateTime expectedEndTime;
    private bool departureAlarmShown;
    private DateTime departureAlarmDate;

    private TextBlock startTimeText;
    private TextBlock endTimeText;
    private TextBlock earnedText;
    private Grid earningsExpandedPanel;
    private Button earningsToggleButton;
    private Border settingsPanel;
    private TextBox startTimeBox;
    private TextBox salaryBox;
    private TextBox hoursBox;
    private TextBox daysBox;
    private CheckBox topmostBox;

    private const double ExpandedHeightValue = 188;
    private const double CollapsedHeightValue = 150;
    private const double SettingsHeightValue = 316;

    public WorkWidgetWindow()
    {
        settingsPath = Path.Combine(ResolveAppDir(), "settings.json");
        attendanceRoot = Path.Combine(baseDir, "attendance");
        iconPath = Path.Combine(baseDir, "assets", "work-widget.ico");

        settings = ReadSettings();
        settings.EarningsCollapsed = true;
        detectedLoginTime = GetSessionLoginTime();
        attendance = GetAttendanceRecord(detectedLoginTime, settings.WorkHoursPerDay);
        loginTime = attendance.LoginTime;
        expectedEndTime = attendance.ExpectedEndTime;

        BuildUi();
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
        notifyIcon.Visible = true;
        Closed += delegate
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
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
        ShowInTaskbar = true;

        Border shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = BrushFrom("#C4CCD6"),
            Background = BrushFrom("#F8FAFC"),
            SnapsToDevicePixels = true
        };
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

        title.Children.Add(new TextBlock
        {
            Text = "근무 위젯",
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#0F172A")
        });

        Button settingsButton = IconButton(null, "설정");
        settingsButton.Content = OptionsIcon();
        settingsButton.Click += delegate
        {
            SyncSettingsFields();
            Height = SettingsHeightValue;
            settingsPanel.Visibility = Visibility.Visible;
        };
        Grid.SetColumn(settingsButton, 1);
        title.Children.Add(settingsButton);

        Button closeButton = IconButton(null, "닫기");
        closeButton.Content = CloseIcon();
        closeButton.Click += delegate { Close(); };
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
        Border divider = new Border { Background = BrushFrom("#CBD5E1"), Margin = new Thickness(0, 12, 0, 12) };
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
            FontWeight = FontWeights.Bold,
            Foreground = BrushFrom("#15803D")
        };
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
        settingsPanel = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = BrushFrom("#C4CCD6"),
            Background = BrushFrom("#F8FAFC"),
            Visibility = Visibility.Collapsed
        };
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
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

        Grid header = new Grid { Background = Brushes.Transparent };
        header.MouseLeftButtonDown += DragWindow;
        grid.Children.Add(header);
        header.Children.Add(new TextBlock
        {
            Text = "설정",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#0F172A"),
            VerticalAlignment = VerticalAlignment.Center
        });

        startTimeBox = AddSettingRow(grid, "출근시간", 1);
        salaryBox = AddSettingRow(grid, "연봉", 2);
        hoursBox = AddSettingRow(grid, "하루 근무시간", 3);
        daysBox = AddSettingRow(grid, "연 근무일수", 4);

        topmostBox = new CheckBox
        {
            Content = "항상 위에 표시",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = BrushFrom("#334155")
        };
        Grid.SetRow(topmostBox, 5);
        grid.Children.Add(topmostBox);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 6);
        grid.Children.Add(buttons);
        Button cancel = new Button { Content = "취소", Width = 72, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
        cancel.Template = CreateTextButtonTemplate("#F8FAFC", "#E2E8F0", "#CBD5E1", "#CBD5E1", "#334155");
        cancel.Click += delegate
        {
            settingsPanel.Visibility = Visibility.Collapsed;
            ApplyEarningsVisibility();
        };
        buttons.Children.Add(cancel);

        Button save = new Button
        {
            Content = "저장",
            Width = 72,
            Cursor = Cursors.Hand
        };
        save.Template = CreateTextButtonTemplate("#0F172A", "#1E293B", "#334155", "#0F172A", "#FFFFFF");
        save.Click += delegate { SaveSettingsFromUi(); };
        buttons.Children.Add(save);
    }

    private TextBox AddSettingRow(Grid grid, string label, int row)
    {
        TextBlock text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushFrom("#334155")
        };
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        TextBox box = new TextBox
        {
            Margin = new Thickness(108, 3, 0, 3),
            Padding = new Thickness(8, 4, 8, 4)
        };
        Grid.SetRow(box, row);
        grid.Children.Add(box);
        return box;
    }

    private void AddTimeColumn(Grid parent, string caption, out TextBlock value, int column)
    {
        StackPanel panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(panel, column);
        parent.Children.Add(panel);
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            TextAlignment = TextAlignment.Center,
            FontSize = 12,
            Foreground = BrushFrom("#64748B")
        });
        value = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#0F172A"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(value);
    }

    private void AddLine(Grid root, int row)
    {
        Border line = new Border { Background = BrushFrom("#CBD5E1") };
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
            Foreground = BrushFrom("#334155"),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        button.Padding = new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Template = CreateIconButtonTemplate();
        return button;
    }

    private ControlTemplate CreateIconButtonTemplate()
    {
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
        over.Setters.Add(new Setter(Border.BackgroundProperty, BrushFrom("#E2E8F0"), "HoverBackground"));
        template.Triggers.Add(over);

        Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, BrushFrom("#CBD5E1"), "HoverBackground"));
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
            Stroke = BrushFrom("#334155"),
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
            Stroke = BrushFrom("#64748B"),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 1 1 L 9 9")
        });
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Stroke = BrushFrom("#64748B"),
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
        Brush fill = BrushFrom("#B6BBCB");

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
            Fill = BrushFrom("#F8FAFC")
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
        double hoursPerDay = Math.Max(0.1, settings.WorkHoursPerDay);
        double daysPerYear = Math.Max(1, settings.WorkDaysPerYear);
        double salary = Math.Max(0, settings.AnnualSalary);
        double elapsedSeconds = Math.Max(0, (DateTime.Now - loginTime).TotalSeconds);
        double earned = elapsedSeconds * salary / (daysPerYear * hoursPerDay * 3600);

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
        topmostBox.IsChecked = settings.Topmost;
    }

    private void SaveSettingsFromUi()
    {
        settings.AnnualSalary = ParseDouble(salaryBox.Text, 0);
        settings.WorkHoursPerDay = Math.Max(0.1, ParseDouble(hoursBox.Text, 9));
        settings.WorkDaysPerYear = Math.Max(1, ParseDouble(daysBox.Text, 260));
        settings.Topmost = topmostBox.IsChecked == true;

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
        settingsPanel.Visibility = Visibility.Collapsed;
        ApplyEarningsVisibility();
        UpdateWidget();
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
            Settings loaded = serializer.Deserialize<Settings>(File.ReadAllText(settingsPath, Encoding.UTF8));
            return loaded ?? defaults;
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

    private static string FormatWon(double value)
    {
        return "₩ " + Math.Floor(Math.Max(0, value)).ToString("N0", CultureInfo.GetCultureInfo("ko-KR"));
    }
}
