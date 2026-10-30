# CloudRunner

CloudRunner is a zero-hosting-cost Windows application runner. Each user runs applications on their own Windows PC; CloudRunner does not provide a centrally hosted Windows desktop.

## Current Phase 1 implementation

- **Controller:** C# WinForms + WebView2.
- **Local execution agent:** separate, hidden WinExe running under the signed-in Windows user.
- **Local IPC:** randomly named Windows named pipe with `PipeOptions.CurrentUserOnly`; the controller keeps the pipe open so the agent can clean up if the controller exits.
- **Release input:** direct HTTPS GitHub Release `.exe` URL plus expected SHA-256.
- **Download checks:** approved HTTPS GitHub release redirect hosts, 100 MB maximum, MZ header check and SHA-256 verification.
- **Process lifecycle:** one application at a time, individual command-line arguments, Windows Job Object ownership with kill-on-close, stop the managed process tree and remove its temporary job directory.
- **Window preview:** WgcSharp / Windows Graphics Capture, targeting the launched process's top-level HWND.
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

1. Click **Start Notepad test**.
2. Wait for the process ID and window handle to appear.
3. Click **Capture application window**.
4. Confirm the preview shows the Notepad window rather than the entire desktop.
5. Click **Stop application**.

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
- Only one managed process is supported in this phase.

**A matching SHA-256 proves file integrity against the checksum supplied by the user; it does not prove that the software is safe.** Run only software from publishers you trust. A standard Windows user token still has access to that user's data and credentials.

The current implementation does not provide a hardened sandbox, per-app restricted token, AppContainer, Windows Sandbox, Hyper-V isolation, or antivirus verdict.

## Window-capture notes

The preview uses Windows Graphics Capture and requests the selected application HWND rather than the whole monitor. Microsoft documents the HWND-based capture interop from Windows 10 build 18362. The capture service currently produces a JPEG still preview on demand.

This phase does **not** yet provide continuous 30-FPS streaming, remote mouse/keyboard control, peer-to-peer WebRTC, or a public session pairing service. Those are separate milestones.

Minimized or closed windows may not yield a frame. Some applications use multiple processes or child windows; the current HostAgent reports the launched process's main top-level window only.

## Zero-cost public distribution

CloudRunner itself can be distributed through GitHub Releases. Every public user runs the app on their own Windows PC. CloudRunner does not consume your work laptop or any central Windows VM.

The later optional remote collaboration feature will use direct WebRTC peer-to-peer connections where network topology permits. A TURN relay would still be needed for some NAT/firewall combinations; those sessions cannot be guaranteed to work on a strict zero-budget plan.

## Third-party components

- Microsoft WebView2 SDK: Microsoft package and runtime.
- WgcSharp 1.1.1: MIT licensed; see [Third-party notices](THIRD-PARTY-NOTICES.md).
- Windows Graphics Capture: Windows platform API.

## Development roadmap

1. **Phase 1:** local HostAgent, verified download, process lifecycle and one-window preview.
2. **Phase 2:** continuous window-frame capture and local rendering without VNC.
3. **Phase 3:** WebRTC peer-to-peer video and input with explicit host consent.
4. **Phase 4:** free-tier signalling/pairing, quotas and abuse controls.
5. **Phase 5:** review hardening, installer, public documentation and publisher trust/safety workflow.
