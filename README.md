# CloudRunner

CloudRunner is a zero-hosting-cost Windows application runner. Each user runs applications on their own Windows PC; CloudRunner does not provide a centrally hosted Windows desktop.

## Current implementation (local execution + continuous preview)

- **Controller:** C# WinForms + WebView2.
- **Local execution agent:** separate, hidden WinExe running under the signed-in Windows user.
- **Local IPC:** randomly named Windows named pipe with `PipeOptions.CurrentUserOnly`; the controller keeps the pipe open so the agent can clean up if the controller exits.
- **Release input:** direct HTTPS GitHub Release `.exe` URL plus expected SHA-256.
- **Download checks:** approved HTTPS GitHub release redirect hosts, 100 MB maximum, MZ header check and SHA-256 verification.
- **Process lifecycle:** one application at a time, individual command-line arguments, Windows Job Object ownership with kill-on-close, stop the managed process tree and remove its temporary job directory.
- **Window preview:** WgcSharp using Windows Graphics Capture first, with DXGI Desktop Duplication fallback, targeting the launched process's top-level HWND.
- **Continuous preview:** sequential local frame capture, rendered in WebView2 without navigating/reloading the page for each frame; currently capped at about 5 frames per second.
- **Cloud compute:** none.
- **Remote video streaming:** not implemented yet.
- **Remote pairing/signalling:** not implemented yet.

## Project layout

    CloudRunnerTest
    ├── .github/workflows
    │   ├── build-cloudrunner-winforms.yml
    │   ├── diagnose-gui-session.yml       # old GitHub runner diagnostic experiment
    │   └── run-release-exe.yml            # headless Actions smoke test; not GUI hosting
    ├── CloudRunner.WinForms
    │   ├── CloudRunner.WinForms.csproj
    │   ├── Program.cs
    │   ├── MainForm.cs
    │   ├── HostAgentClient.cs
    │   ├── WindowsCommandLineParser.cs
    │   └── WindowCaptureService.cs
    └── CloudRunner.HostAgent
        ├── CloudRunner.HostAgent.csproj
        ├── Program.cs
        ├── AgentProtocol.cs
        ├── AgentServer.cs
        ├── ReleaseAssetDownloader.cs
        └── ExecutionSession.cs

## Requirements

- Windows 10 version 2004 (build 19041) or later for this build's Windows SDK target and WgcSharp package.
- .NET 10 SDK to build.
- Microsoft Edge WebView2 Evergreen Runtime.
- A Windows 10/11 x64 machine with a compatible Direct3D 11 graphics device for window capture.
- CloudRunner should be started as a standard, non-elevated user for downloaded EXE execution.

## Build

From the repository root:

    dotnet restore .\CloudRunner.WinForms\CloudRunner.WinForms.csproj
    dotnet build .\CloudRunner.WinForms\CloudRunner.WinForms.csproj -c Release --no-restore

The WinForms project builds the HostAgent project and copies its executable and runtime files into the `Agent` subfolder beside the controller.

Alternatively, run the manual GitHub Actions workflow:

https://github.com/greatPHPer/CloudRunnerTest/actions/workflows/build-cloudrunner-winforms.yml

The workflow uploads a short-retention build artifact.

## Run locally

    dotnet run --project .\CloudRunner.WinForms\CloudRunner.WinForms.csproj

1. Click **Start capture test window**.
2. Wait for the dedicated CloudRunner WinForms test process ID and window handle to appear.
3. Click **Capture application window** to verify a one-frame capture.
4. Click **Start live preview** and confirm that the test window's clock/counter keeps updating in WebView2.
5. Minimize the test window and verify that preview pauses; restore it and verify that preview resumes.
6. Click **Stop live preview**, then **Stop application**.

The test uses a small WinForms application shipped beside HostAgent. It deliberately does not use the modern Windows Notepad launcher: on some Windows 11 installations, `notepad.exe` hands the activation to the packaged Notepad app and exits with code 0, so its original process is not the window-owning process we need to capture.

For a release EXE:

1. Paste a direct GitHub Release asset URL in the URL field.
2. Paste the publisher-provided SHA-256 in the checksum field.
3. Optionally supply command-line arguments.
4. Click **Download, verify and run EXE**.
5. Capture its window after the HostAgent reports a non-zero HWND.

## Execution security

CloudRunner is a launcher, not a malware sandbox.

- Only direct HTTPS GitHub Release asset URLs are accepted.
- Redirects are limited to HTTPS GitHub release asset hosts.
- The downloaded EXE is limited to 100 MB and must have an MZ header.
- The exact expected SHA-256 must be supplied and match.
- Arguments are passed as separate argument entries, not concatenated into a shell command.
- Downloaded executables are not launched with elevation; the agent refuses to run them when it detects an elevated Administrator token.
- The agent restricts its named pipe to the current Windows user.
- The launched process is assigned to a Windows Job Object configured to terminate assigned processes when the HostAgent exits.
- The capture test uses a bundled, standalone WinForms window so the reported process owns the HWND; it does not depend on the Windows Notepad launcher.
- Only one managed process is supported in this phase.

**A matching SHA-256 proves file integrity against the checksum supplied by the user; it does not prove that the software is safe.** Run only software from publishers you trust. A standard Windows user token still has access to that user's data and credentials.

The current implementation does not provide a hardened sandbox, per-app restricted token, AppContainer, Windows Sandbox, Hyper-V isolation, or antivirus verdict.

## Window-capture notes

The preview requests the selected application HWND rather than the whole monitor. WgcSharp tries Windows Graphics Capture first and falls back to DXGI Desktop Duplication if WGC fails on the current device or window. The DXGI fallback captures the visible desktop output and crops it to the window bounds, so the target window must be visible and not minimized. Windows Graphics Capture can capture a window even when another window overlaps it; the DXGI fallback depends on the target being visible in the captured desktop output.

The live-preview loop captures one frame at a time (no overlapping capture tasks), waits about 200 ms between successful frames, and updates the existing WebView2 image element. This is a local preview, currently limited to approximately 5 FPS; it is not a 30-FPS or network video stream. If the target is minimized, the loop pauses and retries when it is restored. If the target closes, or three consecutive capture attempts fail, preview stops. Stopping the application or closing CloudRunner also stops the preview loop.

This phase does **not** yet provide remote mouse/keyboard control, peer-to-peer WebRTC, or a public session pairing service. Those remain separate milestones.

Minimized or closed windows may not yield a frame. Some applications use multiple processes or child windows; the current HostAgent reports the launched process's main top-level window only.

## Experimental manual WebRTC video viewer

The first remote-viewing prototype sends **video only** from the host's existing local preview. It uses the WebView2 canvas capture stream and a browser `RTCPeerConnection`; the canvas track is capped at approximately 5 FPS.

- **Host:** start the application and click **Start live preview**. In the preview's Remote viewing panel, click **Create viewer offer**. Select the offer text and copy it.
- **Viewer:** open the static page in `viewer/index.html` (or the published GitHub Pages site), paste the host offer, and click **Create viewer answer**. Copy the generated answer.
- **Host:** paste the viewer answer into CloudRunner and click **Apply answer / connect**.
- **End:** stop the live preview or click **End remote session** on the host. Stopping local preview closes the WebRTC peer connection.

The exchange is deliberately manual in this prototype; there is no signalling service yet. Send offer/answer text only through a trusted private channel. SDP contains ICE connection details, and anyone who completes the exchange can view the host window while the peer session is active. This prototype does not send mouse, keyboard, clipboard, files, audio, or commands.

### Publish the browser viewer with GitHub Pages

1. In the repository, open **Settings → Pages**.
2. Under **Build and deployment → Source**, choose **GitHub Actions** and save.
3. Open **Actions → Deploy CloudRunner Remote Viewer → Run workflow**.
4. After the deployment succeeds, the viewer should be available at `https://greatphper.github.io/CloudRunnerTest/`.

GitHub Pages is only used to serve the static viewer HTML. It does not carry the video. The workflow is manual so the first deployment is not attempted until Pages has been enabled in repository settings.

### Network limitations

The prototype configures a public STUN server and uses non-trickle ICE by exchanging the complete offer and answer after candidate gathering. It does not include a TURN server. Direct peer-to-peer media works only when the two networks can establish a usable ICE candidate pair; restrictive NATs, VPNs, corporate firewalls, or blocked UDP may prevent connection. A TURN relay may be required for those networks and is not included in the zero-budget prototype. Test on the same LAN first, then on separate networks.

This is an experimental demonstration, not a hardened remote-access product. Treat the SDP exchange as a temporary viewing invitation and end the session when finished.

## Zero-cost public distribution

CloudRunner itself can be distributed through GitHub Releases. Every public user runs the app on their own Windows PC. CloudRunner does not consume your work laptop or any central Windows VM.

The later optional remote collaboration feature will use direct WebRTC peer-to-peer connections where network topology permits. A TURN relay would still be needed for some NAT/firewall combinations; those sessions cannot be guaranteed to work on a strict zero-budget plan.

## Third-party components

- Microsoft WebView2 SDK: Microsoft package and runtime.
- WgcSharp 1.1.1: MIT licensed; see [Third-party notices](THIRD-PARTY-NOTICES.md).
- Windows Graphics Capture: Windows platform API.

## Development roadmap

1. **Phase 1:** local HostAgent, verified download and process lifecycle.
2. **Phase 2:** continuous local window-frame capture and WebView2 rendering (initial implementation is present; performance and compatibility testing remain).
3. **Phase 3:** experimental video-only WebRTC peer-to-peer prototype (manual SDP exchange is now present; cross-network compatibility testing remains).
4. **Phase 4:** signalling/pairing, viewer authentication, TURN strategy, quotas and abuse controls; remote input remains disabled until a separate security review.
5. **Phase 5:** review hardening, installer, public documentation and publisher trust/safety workflow.
