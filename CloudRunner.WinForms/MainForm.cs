using System.Diagnostics;
using Microsoft.Web.WebView2.WinForms;

namespace CloudRunner.WinForms;

internal sealed class MainForm : Form
{
    private readonly HostAgentClient _agent = new();
    private readonly WebView2 _preview = new();
    private readonly TextBox _log = new();
    private readonly TextBox _urlInput = new();
    private readonly TextBox _shaInput = new();
    private readonly TextBox _argumentsInput = new();
    private readonly Label _status = new();
    private readonly Label _windowInfo = new();
    private readonly Button _runButton = new();
    private readonly Button _captureButton = new();

    private long? _activeWindowHandle;
    private bool _webViewReady;
    private bool _shutdownInProgress;
    private bool _allowClose;

    public MainForm()
    {
        Text = "CloudRunner – Local Windows Runner";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 720);
        Size = new Size(1450, 900);
        BackColor = Color.FromArgb(17, 24, 39);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        BuildLayout();
        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;
    }

    private void BuildLayout()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            Padding = new Padding(20, 10, 20, 8),
            BackColor = Color.FromArgb(24, 34, 52)
        };

        header.Controls.Add(new Label
        {
            Text = "CloudRunner",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI Semibold", 17F),
            ForeColor = Color.White
        });
        header.Controls.Add(new Label
        {
            Text = "Run on this Windows PC · Verify releases · Preview the application window",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(174, 190, 210)
        });

        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(1400, 800),
            SplitterDistance = 390,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 350,
            Panel2MinSize = 600,
            BackColor = Color.FromArgb(17, 24, 39),
            BorderStyle = BorderStyle.None
        };

        var left = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            AutoScroll = true,
            BackColor = Color.FromArgb(24, 34, 52)
        };

        left.Controls.Add(MakeLabel("RUN A WINDOWS APPLICATION", 10F, true, 4, 8, 340, 24));

        _urlInput.Multiline = true;
        _urlInput.ScrollBars = ScrollBars.Vertical;
        _urlInput.PlaceholderText = "https://github.com/owner/repo/releases/download/tag/application.exe";
        _urlInput.Height = 54;
        AddField(left, "Direct GitHub Release EXE URL", _urlInput, 42, 64);

        _shaInput.PlaceholderText = "64-character SHA-256";
        AddField(left, "Expected SHA-256", _shaInput, 120, 54);

        _argumentsInput.Multiline = true;
        _argumentsInput.ScrollBars = ScrollBars.Vertical;
        _argumentsInput.PlaceholderText = "Optional arguments, e.g. --mode demo";
        _argumentsInput.Height = 54;
        AddField(left, "Command-line arguments", _argumentsInput, 186, 64);

        _runButton.Text = "Download, verify and run EXE";
        StylePrimaryButton(_runButton);
        _runButton.SetBounds(8, 260, 340, 42);
        _runButton.Click += async (_, _) => await RunReleaseAsync();
        left.Controls.Add(_runButton);

        var notepadButton = MakeButton("Start Notepad test", 312);
        notepadButton.Click += async (_, _) => await StartNotepadAsync();
        left.Controls.Add(notepadButton);

        _captureButton.Text = "Capture application window";
        _captureButton.SetBounds(8, 362, 340, 40);
        _captureButton.Enabled = false;
        StyleSecondaryButton(_captureButton);
        _captureButton.Click += async (_, _) => await CapturePreviewAsync();
        left.Controls.Add(_captureButton);

        var refreshButton = MakeButton("Refresh process / window status", 408);
        refreshButton.Click += async (_, _) => await RefreshStatusAsync();
        left.Controls.Add(refreshButton);

        var stopButton = MakeButton("Stop application", 454);
        stopButton.Click += async (_, _) => await StopApplicationAsync();
        left.Controls.Add(stopButton);

        _status.SetBounds(8, 514, 340, 44);
        _status.Font = new Font("Segoe UI Semibold", 10F);
        _status.ForeColor = Color.Goldenrod;
        _status.Text = "HostAgent not started";
        left.Controls.Add(_status);

        _windowInfo.SetBounds(8, 562, 340, 64);
        _windowInfo.ForeColor = Color.FromArgb(190, 202, 220);
        _windowInfo.Text = "No application window selected.";
        left.Controls.Add(_windowInfo);

        var note = new Label
        {
            Left = 8,
            Top = 638,
            Width = 340,
            Height = 125,
            ForeColor = Color.FromArgb(170, 186, 205),
            Text = "Security: downloaded programs run with your Windows user permissions. " +
                   "Use trusted publishers only. CloudRunner refuses to launch release EXEs when it is running elevated as Administrator. " +
                   "This is not a sandbox."
        };
        left.Controls.Add(note);

        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(950, 800),
            Orientation = Orientation.Horizontal,
            SplitterDistance = 590,
            Panel1MinSize = 300,
            Panel2MinSize = 100,
            BackColor = Color.FromArgb(17, 24, 39)
        };

        _preview.Dock = DockStyle.Fill;
        _preview.DefaultBackgroundColor = Color.FromArgb(10, 15, 25);
        right.Panel1.Controls.Add(_preview);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(10, 15, 25);
        _log.ForeColor = Color.FromArgb(211, 220, 233);
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Consolas", 9F);
        right.Panel2.Controls.Add(_log);

        root.Panel1.Controls.Add(left);
        root.Panel2.Controls.Add(right);

        Controls.Add(root);
        Controls.Add(header);
    }

    private static Label MakeLabel(
        string text, float fontSize, bool bold, int left, int top, int width, int height) => new()
    {
        Text = text,
        Left = left,
        Top = top,
        Width = width,
        Height = height,
        Font = new Font("Segoe UI", fontSize, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = Color.White
    };

    private static void AddField(Panel parent, string caption, TextBox textBox, int labelTop, int boxTop)
    {
        parent.Controls.Add(MakeLabel(caption, 9.5F, true, 8, labelTop, 340, 22));
        textBox.SetBounds(8, boxTop, 340, textBox.Height == 0 ? 30 : textBox.Height);
        textBox.BackColor = Color.FromArgb(12, 19, 32);
        textBox.ForeColor = Color.White;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.Font = new Font("Segoe UI", 9F);
        parent.Controls.Add(textBox);
    }

    private static Button MakeButton(string text, int top)
    {
        var button = new Button { Text = text };
        button.SetBounds(8, top, 340, 40);
        StyleSecondaryButton(button);
        return button;
    }

    private static void StylePrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.FromArgb(37, 99, 235);
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 10F);
        button.Cursor = Cursors.Hand;
    }

    private static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 83, 110);
        button.BackColor = Color.FromArgb(39, 53, 74);
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI", 9.5F);
        button.Cursor = Cursors.Hand;
    }

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        try
        {
            await _preview.EnsureCoreWebView2Async();
            _webViewReady = true;
            _preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _preview.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ShowWelcomePage();
            Log("WebView2 initialized.");
        }
        catch (Exception ex)
        {
            Log("WebView2 initialization failed: " + ex.Message);
            MessageBox.Show(this,
                "Microsoft Edge WebView2 Runtime is required. Install the Evergreen WebView2 Runtime and reopen CloudRunner.\r\n\r\n" + ex.Message,
                "WebView2 unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunReleaseAsync()
    {
        if (string.IsNullOrWhiteSpace(_urlInput.Text) || string.IsNullOrWhiteSpace(_shaInput.Text))
        {
            MessageBox.Show(this, "Enter a direct GitHub Release EXE URL and its expected SHA-256 checksum.",
                "Missing information", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await RunCommandAsync(async () =>
        {
            var arguments = WindowsCommandLineParser.ParseArguments(_argumentsInput.Text);
            Log("Starting HostAgent: downloading release asset and verifying SHA-256...");

            var response = await _agent.SendAsync(new AgentCommand(
                "start",
                _urlInput.Text.Trim(),
                _shaInput.Text.Trim(),
                arguments));

            ApplyAgentResponse(response);
        });
    }

    private async Task StartNotepadAsync()
    {
        await RunCommandAsync(async () =>
        {
            Log("Starting the built-in Notepad test window through HostAgent...");
            var response = await _agent.SendAsync(new AgentCommand("start-notepad"));
            ApplyAgentResponse(response);
        });
    }

    private async Task RefreshStatusAsync()
    {
        await RunCommandAsync(async () =>
        {
            var response = await _agent.SendAsync(new AgentCommand("status"));
            ApplyAgentResponse(response);
        });
    }

    private async Task StopApplicationAsync()
    {
        await RunCommandAsync(async () =>
        {
            var response = await _agent.SendAsync(new AgentCommand("stop"));
            ApplyAgentResponse(response);
            if (response.Success)
            {
                _activeWindowHandle = null;
                _captureButton.Enabled = false;
                ShowWelcomePage();
            }
        });
    }

    private async Task CapturePreviewAsync()
    {
        if (_activeWindowHandle is not long hwnd || hwnd == 0)
        {
            MessageBox.Show(this, "No application window handle is available. Start an application and refresh status.",
                "No capture target", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _captureButton.Enabled = false;
        try
        {
            Log($"Capturing application window HWND=0x{hwnd:X} using Windows Graphics Capture...");
            var jpegBytes = await WindowCaptureService.CaptureJpegAsync(hwnd);
            await ShowCapturedImageAsync(jpegBytes);
            Log($"Window preview captured ({jpegBytes.Length:N0} JPEG bytes).");
        }
        catch (Exception ex)
        {
            Log("Window capture failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Window capture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _captureButton.Enabled = _activeWindowHandle.HasValue;
        }
    }

    private async Task ShowCapturedImageAsync(byte[] jpegBytes)
    {
        if (!_webViewReady)
        {
            await _preview.EnsureCoreWebView2Async();
            _webViewReady = true;
        }

        var base64 = Convert.ToBase64String(jpegBytes);
        _preview.NavigateToString($$"""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <style>
              html,body{height:100%;margin:0;background:#0a0f19;color:#cbd5e1;font:14px 'Segoe UI',sans-serif}
              body{display:grid;place-items:center;overflow:auto}
              img{max-width:100%;max-height:100%;object-fit:contain}
            </style></head>
            <body><img alt="Captured application window" src="data:image/jpeg;base64,{{base64}}"></body></html>
            """);
    }

    private void ApplyAgentResponse(AgentReply response)
    {
        Log(response.Message);
        _status.Text = response.Success ? "● " + response.Message : "○ " + response.Message;
        _status.ForeColor = response.Success ? Color.LightGreen : Color.Salmon;

        if (response.ProcessId is int processId)
        {
            var windowTitle = response.WindowTitle ?? "(title not available)";
            var windowHandleText = response.WindowHandle is long hwnd && hwnd != 0
                ? $"0x{hwnd:X}"
                : "(not ready)";

            _windowInfo.Text = $"Process ID: {processId}\r\n" +
                               $"Window: {windowTitle}\r\n" +
                               $"HWND: {windowHandleText}";
        }
        else
        {
            _windowInfo.Text = "No application window selected.";
        }

        _activeWindowHandle = response.WindowHandle is > 0 ? response.WindowHandle : null;
        _captureButton.Enabled = _activeWindowHandle.HasValue;
    }

    private async Task RunCommandAsync(Func<Task> action)
    {
        SetActionsEnabled(false);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            _status.Text = "○ " + ex.Message;
            _status.ForeColor = Color.Salmon;
            MessageBox.Show(this, ex.Message, "CloudRunner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetActionsEnabled(true);
            _captureButton.Enabled = _activeWindowHandle.HasValue;
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        _runButton.Enabled = enabled;
        _captureButton.Enabled = enabled && _activeWindowHandle.HasValue;
    }

    private void ShowWelcomePage()
    {
        if (!_webViewReady)
        {
            return;
        }

        _preview.NavigateToString("""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <style>
              html,body{height:100%;margin:0;background:#0a0f19;color:#d8e2f0;font:16px 'Segoe UI',sans-serif}
              main{height:100%;display:grid;place-content:center;text-align:center}
              h2{margin:0 0 12px;color:#fff;font-weight:600}
              p{max-width:560px;line-height:1.65;color:#aab9cd}
              code{color:#93c5fd}
            </style></head>
            <body><main><h2>CloudRunner application preview</h2>
            <p>Start Notepad for a quick local test, or provide a trusted GitHub Release EXE URL and SHA-256.</p>
            <p>After the application opens, click <b>Capture application window</b>.</p>
            <p>This is a single-window still preview. Continuous video streaming is a later milestone.</p>
            </main></body></html>
            """);
    }

    private void Log(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        Enabled = false;

        try
        {
            await _agent.DisposeAsync();
        }
        catch
        {
            // Closing should not be blocked by an unresponsive child agent.
        }

        _allowClose = true;
        Close();
    }
}
