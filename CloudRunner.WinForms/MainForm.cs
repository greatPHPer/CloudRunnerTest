using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
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
    private readonly Button _startPreviewButton = new();
    private readonly Button _stopPreviewButton = new();

    private long? _activeWindowHandle;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;

    private static readonly TimeSpan PreviewFrameDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MinimizedRetryDelay = TimeSpan.FromMilliseconds(500);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SwRestore = 9;
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
        _shaInput.Height = 28;
        AddField(left, "Expected SHA-256", _shaInput, 126, 148);

        _argumentsInput.Multiline = true;
        _argumentsInput.ScrollBars = ScrollBars.Vertical;
        _argumentsInput.PlaceholderText = "Optional arguments, e.g. --mode demo";
        _argumentsInput.Height = 54;
        AddField(left, "Command-line arguments", _argumentsInput, 184, 206);

        _runButton.Text = "Download, verify and run EXE";
        StylePrimaryButton(_runButton);
        _runButton.SetBounds(8, 270, 340, 42);
        _runButton.Click += async (_, _) => await RunReleaseAsync();
        left.Controls.Add(_runButton);

        var testWindowButton = MakeButton("Start capture test window", 318);
        testWindowButton.Click += async (_, _) => await StartTestWindowAsync();
        left.Controls.Add(testWindowButton);

        _captureButton.Text = "Capture application window";
        _captureButton.SetBounds(8, 364, 340, 40);
        _captureButton.Enabled = false;
        StyleSecondaryButton(_captureButton);
        _captureButton.Click += async (_, _) => await CapturePreviewAsync();
        left.Controls.Add(_captureButton);

        _startPreviewButton.Text = "Start live preview";
        _startPreviewButton.SetBounds(8, 410, 340, 40);
        StyleSecondaryButton(_startPreviewButton);
        _startPreviewButton.Enabled = false;
        _startPreviewButton.Click += async (_, _) => await StartLivePreviewAsync();
        left.Controls.Add(_startPreviewButton);

        _stopPreviewButton.Text = "Stop live preview";
        _stopPreviewButton.SetBounds(8, 456, 340, 40);
        StyleSecondaryButton(_stopPreviewButton);
        _stopPreviewButton.Enabled = false;
        _stopPreviewButton.Click += async (_, _) => await StopLivePreviewAsync();
        left.Controls.Add(_stopPreviewButton);

        var refreshButton = MakeButton("Refresh process / window status", 502);
        refreshButton.Click += async (_, _) => await RefreshStatusAsync();
        left.Controls.Add(refreshButton);

        var stopButton = MakeButton("Stop application", 548);
        stopButton.Click += async (_, _) => await StopApplicationAsync();
        left.Controls.Add(stopButton);

        _status.SetBounds(8, 604, 340, 48);
        _status.Font = new Font("Segoe UI Semibold", 10F);
        _status.ForeColor = Color.Goldenrod;
        _status.Text = "HostAgent not started";
        left.Controls.Add(_status);

        _windowInfo.SetBounds(8, 660, 340, 64);
        _windowInfo.ForeColor = Color.FromArgb(190, 202, 220);
        _windowInfo.Text = "No application window selected.";
        left.Controls.Add(_windowInfo);

        var note = new Label
        {
            Left = 8,
            Top = 734,
            Width = 340,
            Height = 140,
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
        await StopLivePreviewAsync();

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

    private async Task StartTestWindowAsync()
    {
        await StopLivePreviewAsync();

        await RunCommandAsync(async () =>
        {
            Log("Starting the dedicated CloudRunner WinForms capture test window through HostAgent...");
            var response = await _agent.SendAsync(new AgentCommand("start-test-window"));
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
        await StopLivePreviewAsync();

        await RunCommandAsync(async () =>
        {
            var response = await _agent.SendAsync(new AgentCommand("stop"));
            ApplyAgentResponse(response);
            if (response.Success)
            {
                _activeWindowHandle = null;
                _captureButton.Enabled = false;
                _startPreviewButton.Enabled = false;
                _stopPreviewButton.Enabled = false;
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

        await StopLivePreviewAsync();
        _captureButton.Enabled = false;
        var previousWindowState = WindowState;
        try
        {
            Log($"Capturing application window HWND=0x{hwnd:X} (WGC with DXGI fallback)...");
            // DXGI fallback captures the visible desktop and crops to the target.
            // Minimize this controller and foreground the target so the fallback
            // does not accidentally capture CloudRunner over the application.
            ShowWindowAsync(new IntPtr(hwnd), SwRestore);
            SetForegroundWindow(new IntPtr(hwnd));
            await Task.Delay(200);
            WindowState = FormWindowState.Minimized;
            await Task.Delay(250);

            var jpegBytes = await WindowCaptureService.CaptureJpegAsync(hwnd);
            WindowState = previousWindowState;
            Activate();

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
            if (WindowState == FormWindowState.Minimized && previousWindowState != FormWindowState.Minimized)
            {
                WindowState = previousWindowState;
                Activate();
            }

            _captureButton.Enabled = _activeWindowHandle.HasValue && _previewCts is null;
            _startPreviewButton.Enabled = _activeWindowHandle.HasValue && _previewCts is null;
        }
    }

    private Task StartLivePreviewAsync()
    {
        if (_activeWindowHandle is not long hwnd || hwnd == 0)
        {
            MessageBox.Show(this, "No application window handle is available. Start an application and refresh status.",
                "No preview target", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return Task.CompletedTask;
        }

        if (_previewTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _captureButton.Enabled = false;
        _startPreviewButton.Enabled = false;
        _stopPreviewButton.Enabled = true;

        Log($"Starting continuous preview for HWND=0x{hwnd:X} (up to about 5 frames/second)...");
        _previewTask = RunLivePreviewLoopAsync(hwnd, cts);
        return Task.CompletedTask;
    }

    private async Task StopLivePreviewAsync()
    {
        var cts = _previewCts;
        var task = _previewTask;

        if (cts is null)
        {
            return;
        }

        Log("Stopping live preview...");
        cts.Cancel();

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // The preview loop is expected to finish after cancellation.
            }
        }
    }

    private async Task RunLivePreviewLoopAsync(long hwnd, CancellationTokenSource cts)
    {
        var token = cts.Token;

        try
        {
            await EnsureLivePreviewShellAsync(token);
            Log("Live preview is running. The target window must remain open and unminimized.");

            var consecutiveFailures = 0;
            var minimizedMessageLogged = false;

            while (!token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();

                var target = new IntPtr(hwnd);
                if (!IsWindow(target))
                {
                    Log("The target application window has closed. Live preview is stopping.");
                    break;
                }

                if (IsIconic(target))
                {
                    if (!minimizedMessageLogged)
                    {
                        Log("Target window is minimized; preview is paused until it is restored.");
                        minimizedMessageLogged = true;
                    }

                    await Task.Delay(MinimizedRetryDelay, token);
                    continue;
                }

                try
                {
                    var jpegBytes = await WindowCaptureService.CaptureJpegAsync(hwnd, token);
                    token.ThrowIfCancellationRequested();
                    await UpdateLivePreviewFrameAsync(jpegBytes);
                    consecutiveFailures = 0;
                    minimizedMessageLogged = false;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    Log($"Preview frame failed ({consecutiveFailures}/3): {ex.Message}");

                    if (consecutiveFailures >= 3)
                    {
                        Log("Live preview stopped after three consecutive capture failures. Check that the target window is visible and available for capture.");
                        break;
                    }

                    await Task.Delay(MinimizedRetryDelay, token);
                }

                await Task.Delay(PreviewFrameDelay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal Stop Preview path.
        }
        catch (Exception ex)
        {
            Log("Live preview stopped: " + ex.Message);
        }
        finally
        {
            try
            {
                if (_preview.CoreWebView2 is not null)
                {
                    await _preview.ExecuteScriptAsync("window.cloudRunnerClosePeer?.();");
                }
            }
            catch
            {
                // The WebView may already be shutting down; the local preview still needs to finish stopping.
            }

            if (ReferenceEquals(_previewCts, cts))
            {
                _previewCts = null;
                _previewTask = null;
                cts.Dispose();

                if (!IsDisposed)
                {
                    _stopPreviewButton.Enabled = false;
                    _startPreviewButton.Enabled = _activeWindowHandle.HasValue;
                    _captureButton.Enabled = _activeWindowHandle.HasValue;
                    Log("Live preview stopped.");
                }
            }
            else
            {
                cts.Dispose();
            }
        }
    }

    private async Task EnsureLivePreviewShellAsync(CancellationToken cancellationToken)
    {
        if (!_webViewReady)
        {
            await _preview.EnsureCoreWebView2Async();
            _webViewReady = true;
        }

        var core = _preview.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 is not ready.");

        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            core.NavigationCompleted -= handler;
            if (args.IsSuccess)
            {
                navigation.TrySetResult(true);
            }
            else
            {
                navigation.TrySetException(new InvalidOperationException(
                    $"WebView2 could not initialize the live preview surface (web error: {args.WebErrorStatus})."));
            }
        };

        core.NavigationCompleted += handler;
        try
        {
            _preview.NavigateToString("""
                <!doctype html>
                <html lang="en">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <title>CloudRunner local preview</title>
                  <style>
                    *{box-sizing:border-box}
                    html,body{height:100%;margin:0;background:#0a0f19;color:#dbe6f5;font:14px 'Segoe UI',sans-serif}
                    #layout{height:100%;display:grid;grid-template-columns:minmax(0,1fr) 310px;min-width:0}
                    #stage{position:relative;display:grid;place-items:center;min-width:0;min-height:0;overflow:hidden;background:#050a12}
                    #local-frame{display:block;max-width:100%;max-height:100%;width:auto;height:auto;object-fit:contain}
                    #hint{position:absolute;left:16px;bottom:16px;padding:8px 11px;background:#101827e8;border:1px solid #26364d;border-radius:7px;color:#b5c5da}
                    #host-label{position:absolute;top:12px;left:14px;padding:5px 8px;background:#142238e8;border-radius:5px;color:#93c5fd;font-size:11px;font-weight:700;letter-spacing:.06em}
                    #panel{min-width:0;overflow:auto;padding:16px;background:#111b2b;border-left:1px solid #26364d}
                    h2{font-size:17px;margin:0 0 7px;color:#fff}
                    p{color:#afbed2;line-height:1.45;margin:7px 0 12px}
                    label{display:block;font-weight:600;margin:12px 0 6px;color:#dbe6f5}
                    textarea{display:block;width:100%;min-height:94px;resize:vertical;border:1px solid #3a4b64;border-radius:6px;background:#07111f;color:#dbeafe;padding:8px;font:11px Consolas,monospace}
                    button{width:100%;border:0;border-radius:6px;padding:9px 10px;margin:4px 0;background:#2563eb;color:white;font-weight:600;cursor:pointer}
                    button.secondary{background:#31435c}
                    button.danger{background:#7f1d1d}
                    button:disabled{opacity:.5;cursor:not-allowed}
                    #peer-status{padding:9px;border-radius:6px;background:#091424;color:#a7f3d0;min-height:38px;overflow-wrap:anywhere}
                    .warning{font-size:11px;color:#facc9b;line-height:1.45;border:1px solid #704c2f;border-radius:6px;padding:9px;margin-top:12px}
                    .small{font-size:11px;color:#8fa4bf}
                    @media(max-width:760px){#layout{grid-template-columns:minmax(0,1fr)}#panel{max-height:46%;border-left:0;border-top:1px solid #26364d}#stage{min-height:54%}}
                  </style>
                </head>
                <body>
                  <div id="layout">
                    <section id="stage">
                      <canvas id="local-frame" aria-label="Local application preview"></canvas>
                      <div id="hint">Waiting for first captured frame…</div>
                      <div id="host-label">HOST · LOCAL CAPTURE</div>
                    </section>
                    <aside id="panel">
                      <h2>Remote viewing</h2>
                      <p>Experimental, video-only WebRTC. Start live preview, create an offer, and exchange the text with one trusted viewer.</p>
                      <button id="create-offer">1. Create viewer offer</button>
                      <label for="offer">Host offer (copy this to the viewer)</label>
                      <textarea id="offer" spellcheck="false" readonly placeholder="Create an offer first…"></textarea>
                      <button id="select-offer" class="secondary">Select offer text for copying</button>
                      <label for="answer">Viewer answer (paste it here)</label>
                      <textarea id="answer" spellcheck="false" placeholder="Paste the answer received from the viewer…"></textarea>
                      <button id="apply-answer">2. Apply answer / connect</button>
                      <p id="peer-status" role="status">No remote viewer connected.</p>
                      <button id="end-session" class="danger">End remote session</button>
                      <p class="small">This prototype uses a public STUN server and manual offer/answer exchange. Some networks require a TURN relay and may not connect.</p>
                      <div class="warning">Share the offer only with a person you trust. It contains connection details and allows its recipient to view the application window while the session is active. No mouse or keyboard control is exposed.</div>
                    </aside>
                  </div>
                  <script>
                    (() => {
                      const canvas = document.getElementById('local-frame');
                      const context = canvas.getContext('2d');
                      const hint = document.getElementById('hint');
                      const offerBox = document.getElementById('offer');
                      const answerBox = document.getElementById('answer');
                      const status = document.getElementById('peer-status');
                      const createButton = document.getElementById('create-offer');
                      const applyButton = document.getElementById('apply-answer');
                      let peerConnection = null;
                      let outboundStream = null;

                      function setStatus(message) {
                        status.textContent = message;
                      }

                      function waitForIceGatheringComplete(pc, timeoutMs = 30000) {
                        if (pc.iceGatheringState === 'complete') return Promise.resolve();
                        return new Promise((resolve, reject) => {
                          const finish = (error) => {
                            clearTimeout(timer);
                            pc.removeEventListener('icegatheringstatechange', changed);
                            if (error) reject(error); else resolve();
                          };
                          const changed = () => {
                            if (pc.iceGatheringState === 'complete') finish();
                          };
                          const timer = setTimeout(
                            () => finish(new Error('ICE candidate gathering timed out. Try again or test both PCs on the same network.')),
                            timeoutMs);
                          pc.addEventListener('icegatheringstatechange', changed);
                        });
                      }

                      function closePeerConnection() {
                        if (peerConnection) {
                          peerConnection.close();
                          peerConnection = null;
                        }
                        if (outboundStream) {
                          outboundStream.getTracks().forEach(track => track.stop());
                          outboundStream = null;
                        }
                        setStatus('Remote session ended.');
                      }

                      window.cloudRunnerSetFrame = (dataUri) => {
                        const image = new Image();
                        image.onload = () => {
                          if (canvas.width !== image.naturalWidth || canvas.height !== image.naturalHeight) {
                            canvas.width = image.naturalWidth;
                            canvas.height = image.naturalHeight;
                          }
                          context.drawImage(image, 0, 0, canvas.width, canvas.height);
                          hint.style.display = 'none';
                        };
                        image.onerror = () => setStatus('A local preview frame could not be decoded.');
                        image.src = dataUri;
                      };

                      window.cloudRunnerClosePeer = closePeerConnection;

                      createButton.addEventListener('click', async () => {
                        createButton.disabled = true;
                        try {
                          if (!canvas.width || !canvas.height || hint.style.display !== 'none') {
                            throw new Error('Start live preview and wait for the first frame before creating an offer.');
                          }

                          closePeerConnection();
                          offerBox.value = '';
                          answerBox.value = '';
                          setStatus('Creating WebRTC offer…');

                          const pc = new RTCPeerConnection({
                            iceServers: [{ urls: 'stun:stun.l.google.com:19302' }]
                          });
                          peerConnection = pc;
                          outboundStream = canvas.captureStream(5);
                          outboundStream.getTracks().forEach(track => pc.addTrack(track, outboundStream));

                          pc.addEventListener('connectionstatechange', () => {
                            if (peerConnection !== pc) return;
                            setStatus('Peer connection: ' + pc.connectionState);
                          });
                          pc.addEventListener('iceconnectionstatechange', () => {
                            if (peerConnection !== pc) return;
                            if (pc.iceConnectionState === 'failed') {
                              setStatus('ICE connection failed. This network may require a TURN relay.');
                            }
                          });

                          const offer = await pc.createOffer();
                          await pc.setLocalDescription(offer);
                          setStatus('Gathering network candidates. Please wait…');
                          await waitForIceGatheringComplete(pc);

                          if (peerConnection !== pc) throw new Error('The remote session was ended while creating the offer.');
                          offerBox.value = JSON.stringify(pc.localDescription);
                          setStatus('Offer ready. Copy it to the trusted viewer and wait for the answer.');
                        } catch (error) {
                          setStatus('Offer failed: ' + (error?.message || String(error)));
                          closePeerConnection();
                        } finally {
                          createButton.disabled = false;
                        }
                      });

                      document.getElementById('select-offer').addEventListener('click', () => {
                        if (!offerBox.value) {
                          setStatus('Create an offer first.');
                          return;
                        }
                        offerBox.focus();
                        offerBox.select();
                        setStatus('Offer selected. Press Ctrl+C to copy it.');
                      });

                      applyButton.addEventListener('click', async () => {
                        applyButton.disabled = true;
                        try {
                          if (!peerConnection) throw new Error('Create a new offer before applying the answer.');
                          const answer = JSON.parse(answerBox.value);
                          if (answer?.type !== 'answer' || typeof answer.sdp !== 'string') {
                            throw new Error('The pasted text is not a valid WebRTC answer.');
                          }
                          await peerConnection.setRemoteDescription(answer);
                          setStatus('Answer accepted. Waiting for the peer-to-peer video connection…');
                        } catch (error) {
                          setStatus('Could not apply answer: ' + (error?.message || String(error)));
                        } finally {
                          applyButton.disabled = false;
                        }
                      });

                      document.getElementById('end-session').addEventListener('click', () => {
                        closePeerConnection();
                        offerBox.value = '';
                        answerBox.value = '';
                      });
                    })();
                  </script>
                </body>
                </html>
                """);

            await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally
        {
            core.NavigationCompleted -= handler;
        }
    }

    private async Task UpdateLivePreviewFrameAsync(byte[] jpegBytes)
    {
        var core = _preview.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 is not ready to display preview frames.");

        var dataUri = "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes);
        var encodedDataUri = JsonSerializer.Serialize(dataUri);
        await core.ExecuteScriptAsync($"window.cloudRunnerSetFrame({encodedDataUri});");
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
            _activeWindowHandle = response.WindowHandle is > 0 ? response.WindowHandle : null;
        }
        else if (response.Success)
        {
            _windowInfo.Text = "No application window selected.";
            _activeWindowHandle = null;
        }

        // On a failed request without process details, retain the last known active window.
        _captureButton.Enabled = _activeWindowHandle.HasValue && _previewCts is null;
        _startPreviewButton.Enabled = _activeWindowHandle.HasValue && _previewCts is null;
        _stopPreviewButton.Enabled = _previewCts is not null;
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
        _captureButton.Enabled = enabled && _activeWindowHandle.HasValue && _previewCts is null;
        _startPreviewButton.Enabled = enabled && _activeWindowHandle.HasValue && _previewCts is null;
        _stopPreviewButton.Enabled = _previewCts is not null;
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
            <p>Start the dedicated capture test window, or provide a trusted GitHub Release EXE URL and SHA-256.</p>
            <p>After the application opens, click <b>Capture application window</b> for one frame, or <b>Start live preview</b> for continuous local updates.</p>
            <p>Live preview is currently capped at about 5 frames per second. The experimental remote-viewing panel can send this preview to one browser viewer over WebRTC.</p>
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
            await StopLivePreviewAsync();
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
