using System.Diagnostics;
using Microsoft.Web.WebView2.WinForms;

namespace CloudRunner.WinForms;

internal sealed class MainForm : Form
{
    private readonly LocalStreamingBridge _bridge = new();
    private readonly WebView2 _viewer = new();
    private readonly TextBox _log = new();
    private readonly Label _vncStatus = new();
    private readonly Label _toolsStatus = new();
    private readonly Label _bridgeStatus = new();
    private readonly Button _connectButton = new();
    private bool _viewerInitialized;

    public MainForm()
    {
        Text = "CloudRunner – Local GUI Streaming Prototype";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1050, 680);
        Size = new Size(1350, 850);
        BackColor = Color.FromArgb(18, 24, 38);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        BuildLayout();
        Shown += MainForm_Shown;
        FormClosed += (_, _) => _bridge.Dispose();
    }

    private void BuildLayout()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 66,
            Padding = new Padding(18, 10, 18, 8),
            BackColor = Color.FromArgb(24, 34, 52)
        };

        var title = new Label
        {
            Text = "CloudRunner",
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI Semibold", 16F),
            ForeColor = Color.White
        };
        var subtitle = new Label
        {
            Text = "Local Windows desktop streaming · WebView2 · localhost only",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(170, 185, 205)
        };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(1300, 760),
            FixedPanel = FixedPanel.Panel1,
            BackColor = Color.FromArgb(18, 24, 38),
            BorderStyle = BorderStyle.None,
            Panel1MinSize = 280,
            Panel2MinSize = 500,
            SplitterDistance = 310
        };

        var left = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = Color.FromArgb(24, 34, 52),
            AutoScroll = true
        };

        var heading = MakeLabel("SESSION SETUP", 10, true);
        heading.Top = 8;
        heading.Left = 4;
        heading.Width = 260;
        left.Controls.Add(heading);

        AddStatus(left, "TightVNC server (127.0.0.1:5900)", _vncStatus, 48);
        AddStatus(left, "noVNC + websockify tools", _toolsStatus, 105);
        AddStatus(left, "WebSocket bridge (127.0.0.1:6080)", _bridgeStatus, 162);

        var setupButton = MakeButton("Install noVNC + websockify", 232);
        setupButton.Click += (_, _) => RunSetupScript();
        left.Controls.Add(setupButton);

        var tightVncButton = MakeButton("Open TightVNC download", 278);
        tightVncButton.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("https://www.tightvnc.com/download/") { UseShellExecute = true });
        left.Controls.Add(tightVncButton);

        var notepadButton = MakeButton("Open Notepad test app", 324);
        notepadButton.Click += (_, _) => StartNotepad();
        left.Controls.Add(notepadButton);

        _connectButton.Text = "Start local viewer";
        _connectButton.SetBounds(8, 382, 260, 40);
        _connectButton.FlatStyle = FlatStyle.Flat;
        _connectButton.FlatAppearance.BorderSize = 0;
        _connectButton.BackColor = Color.FromArgb(37, 99, 235);
        _connectButton.ForeColor = Color.White;
        _connectButton.Font = new Font("Segoe UI Semibold", 10F);
        _connectButton.Click += async (_, _) => await StartViewerAsync();
        left.Controls.Add(_connectButton);

        var stopButton = MakeButton("Stop viewer / bridge", 430);
        stopButton.Click += (_, _) => StopViewer();
        left.Controls.Add(stopButton);

        var refreshButton = MakeButton("Refresh prerequisites", 476);
        refreshButton.Click += async (_, _) => await RefreshStatusAsync();
        left.Controls.Add(refreshButton);

        var note = new Label
        {
            Left = 8,
            Top = 540,
            Width = 260,
            Height = 205,
            ForeColor = Color.FromArgb(185, 198, 215),
            Text = "Before connecting:\r\n" +
                   "1. Install TightVNC 4.\r\n" +
                   "2. Start its Server in application mode.\r\n" +
                   "3. Set a VNC password.\r\n" +
                   "4. Restrict access to loopback only.\r\n\r\n" +
                   "CloudRunner does not verify the VNC access-control setting yet.\r\n\r\n" +
                   "Same-PC note: whole-desktop capture can mirror this viewer. Clean app-only capture is a later milestone.\r\n\r\n" +
                   "No VNC password is stored in the viewer URL."
        };
        left.Controls.Add(note);

        var viewerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(1000, 760),
            Orientation = Orientation.Horizontal,
            Panel1MinSize = 300,
            Panel2MinSize = 90,
            BackColor = Color.FromArgb(18, 24, 38),
            SplitterDistance = 570
        };

        _viewer.Dock = DockStyle.Fill;
        _viewer.DefaultBackgroundColor = Color.FromArgb(12, 16, 25);
        viewerSplit.Panel1.Controls.Add(_viewer);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(12, 16, 25);
        _log.ForeColor = Color.FromArgb(205, 215, 230);
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Consolas", 9F);
        viewerSplit.Panel2.Controls.Add(_log);

        root.Panel1.Controls.Add(left);
        root.Panel2.Controls.Add(viewerSplit);

        Controls.Add(root);
        Controls.Add(header);
    }

    private static Label MakeLabel(string text, float size, bool bold) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = Color.White
    };

    private static Button MakeButton(string text, int top) => new()
    {
        Text = text,
        Left = 8,
        Top = top,
        Width = 260,
        Height = 38,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(42, 55, 76),
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 9.5F)
    };

    private static void AddStatus(Control parent, string caption, Label status, int top)
    {
        var label = new Label
        {
            Text = caption,
            Left = 8,
            Top = top,
            Width = 260,
            Height = 20,
            ForeColor = Color.FromArgb(185, 198, 215)
        };
        status.SetBounds(8, top + 21, 260, 24);
        status.Font = new Font("Segoe UI Semibold", 9F);
        status.ForeColor = Color.Goldenrod;
        status.Text = "Checking...";
        parent.Controls.Add(label);
        parent.Controls.Add(status);
    }

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        try
        {
            await _viewer.EnsureCoreWebView2Async();
            _viewerInitialized = true;
            _viewer.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _viewer.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _viewer.CoreWebView2.Settings.IsStatusBarEnabled = false;
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

        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var vnc = await _bridge.IsVncServerAvailableAsync();
        _vncStatus.Text = vnc ? "● Detected on localhost" : "○ Not detected";
        _vncStatus.ForeColor = vnc ? Color.LightGreen : Color.Orange;

        var tools = _bridge.ToolsInstalled;
        _toolsStatus.Text = tools ? "● Installed" : "○ Not installed";
        _toolsStatus.ForeColor = tools ? Color.LightGreen : Color.Orange;

        var bridge = await PortIsOpenAsync(LocalStreamingBridge.WebSocketPort);
        _bridgeStatus.Text = bridge ? "● Port 6080 is listening" : "○ Stopped";
        _bridgeStatus.ForeColor = bridge ? Color.LightGreen : Color.Orange;

        Log($"Prerequisites refreshed: TightVNC={vnc}, tools={tools}, port6080={bridge}.");
    }

    private async Task StartViewerAsync()
    {
        _connectButton.Enabled = false;
        try
        {
            Log("Checking local VNC server and starting WebSocket bridge...");
            await _bridge.StartAsync();
            await _viewer.EnsureCoreWebView2Async();
            _viewerInitialized = true;
            _viewer.Source = new Uri(_bridge.ViewerUrl);
            Log("Viewer opened. Enter the TightVNC password in the noVNC prompt.");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Log("Could not start viewer: " + ex.Message);
            MessageBox.Show(this, ex.Message, "CloudRunner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _connectButton.Enabled = true;
        }
    }

    private void StopViewer()
    {
        _bridge.Stop();
        if (_viewerInitialized)
            ShowWelcomePage();
        Log("WebSocket bridge stopped. TightVNC server was not stopped.");
        _ = RefreshStatusAsync();
    }

    private void ShowWelcomePage()
    {
        if (!_viewerInitialized)
            return;

        _viewer.NavigateToString("""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <style>
              html,body{height:100%;margin:0;background:#0c1019;color:#d5deeb;font:16px 'Segoe UI',sans-serif}
              main{height:100%;display:grid;place-content:center;text-align:center}
              h2{margin:0 0 12px;color:white;font-weight:600}
              p{max-width:520px;line-height:1.6;color:#aab8cb}
              code{color:#93c5fd}
            </style></head>
            <body><main><h2>CloudRunner local viewer</h2>
            <p>Start TightVNC in application mode, then click <b>Start local viewer</b>.<br>
            The noVNC page will ask for your VNC password.</p>
            <p>Connections are configured for <code>127.0.0.1</code> only.</p>
            </main></body></html>
            """);
    }

    private void StartNotepad()
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            Log("Started Notepad. It should be visible on the Windows desktop.");
        }
        catch (Exception ex)
        {
            Log("Could not start Notepad: " + ex.Message);
        }
    }

    private void RunSetupScript()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Setup-LocalStreaming.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show(this,
                "Setup-LocalStreaming.ps1 was not found. Run it from the repository's scripts folder.",
                "Setup script missing", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = true
            });
            Log("Started the local streaming tools setup script.");
        }
        catch (Exception ex)
        {
            Log("Could not start setup: " + ex.Message);
        }
    }

    private static async Task<bool> PortIsOpenAsync(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        using var timeout = new CancellationTokenSource(500);
        try
        {
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Log(string message)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Log(message)));
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
