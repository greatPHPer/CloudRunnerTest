namespace CloudRunner.TestWindow;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TestWindowForm());
    }
}

internal sealed class TestWindowForm : Form
{
    private readonly Label _clockLabel = new();
    private readonly Label _instructionsLabel = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private int _ticks;

    public TestWindowForm()
    {
        Text = "CloudRunner Capture Test Window";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 390);
        Size = new Size(780, 480);
        BackColor = Color.FromArgb(18, 29, 48);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            Text = "CloudRunner window capture is alive",
            Dock = DockStyle.Top,
            Height = 74,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 19F),
            ForeColor = Color.White
        };

        _instructionsLabel.Text =
            "This is a dedicated WinForms test process.\r\n" +
            "CloudRunner should capture this window only, not the entire desktop.";
        _instructionsLabel.Dock = DockStyle.Top;
        _instructionsLabel.Height = 86;
        _instructionsLabel.TextAlign = ContentAlignment.MiddleCenter;
        _instructionsLabel.ForeColor = Color.FromArgb(192, 207, 226);

        _clockLabel.Dock = DockStyle.Top;
        _clockLabel.Height = 60;
        _clockLabel.TextAlign = ContentAlignment.MiddleCenter;
        _clockLabel.Font = new Font("Consolas", 16F, FontStyle.Bold);
        _clockLabel.ForeColor = Color.FromArgb(110, 220, 180);

        var closeButton = new Button
        {
            Text = "Close test window",
            Width = 210,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10F),
            Anchor = AnchorStyles.None
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => Close();

        var buttonPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20)
        };
        closeButton.Location = new Point(
            Math.Max(0, (buttonPanel.ClientSize.Width - closeButton.Width) / 2),
            22);
        buttonPanel.Resize += (_, _) =>
            closeButton.Location = new Point(
                Math.Max(0, (buttonPanel.ClientSize.Width - closeButton.Width) / 2),
                22);
        buttonPanel.Controls.Add(closeButton);

        Controls.Add(buttonPanel);
        Controls.Add(_clockLabel);
        Controls.Add(_instructionsLabel);
        Controls.Add(title);

        _timer.Interval = 1000;
        _timer.Tick += (_, _) => UpdateClock();
        _timer.Start();
        UpdateClock();
    }

    private void UpdateClock()
    {
        _ticks++;
        _clockLabel.Text = $"Live window test · {DateTime.Now:HH:mm:ss} · Frame marker {_ticks}";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
