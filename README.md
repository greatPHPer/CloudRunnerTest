# CloudRunner

CloudRunner is an experimental WinForms application for displaying and controlling a Windows desktop through WebView2.

## Current direction

- GUI streaming prototype: local interactive Windows desktop.
- Viewer: WinForms + Microsoft Edge WebView2.
- Screen server: TightVNC Server for Windows.
- Browser viewer: noVNC.
- WebSocket bridge: websockify.
- Network scope: localhost only for this prototype.
- GitHub Actions: retain the existing workflow for headless EXE smoke tests; do not use hosted Actions runners as a live GUI hosting service.

## Repository layout

    CloudRunnerTest
    ├── .github/workflows
    │   ├── diagnose-gui-session.yml
    │   └── run-release-exe.yml
    ├── CloudRunner.WinForms
    │   ├── CloudRunner.WinForms.csproj
    │   ├── Program.cs
    │   ├── MainForm.cs
    │   └── LocalStreamingBridge.cs
    └── scripts
        └── Setup-LocalStreaming.ps1

## Requirements

- Windows 10/11 x64.
- .NET 10 SDK to build the project.
- Microsoft Edge WebView2 Runtime (Evergreen).
- Python 3 installed with the Python launcher (py.exe) or python.exe.
- TightVNC Server for Windows, installed separately.

## Setup

1. Install TightVNC from the official download page: https://www.tightvnc.com/download/
2. Run TightVNC Server in application mode for the logged-in Windows user. Do not use a Windows service for this first interactive-session test.
3. Configure TightVNC to use its **traditional/classical VNC authentication** compatibility mode for this noVNC prototype. TightVNC 4's newer SRP-6a authentication is not listed in noVNC's documented supported authentication methods. Classical VNC authentication has an eight-character password limit; use it only for this localhost-only test.
4. Set a VNC password.
5. In TightVNC's access-control settings, allow loopback/local connections only. Do not expose TCP 5900 to your LAN or the internet. Do not configure router port forwarding.
5. From the repository root, run:

       powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Setup-LocalStreaming.ps1

   This creates a private Python virtual environment under the local app-data CloudRunner folder, installs websockify 0.13.0 into that environment, and downloads noVNC 1.7.0. It does not install Python or TightVNC globally.

6. Build and run:

       dotnet restore .\CloudRunner.WinForms\CloudRunner.WinForms.csproj
       dotnet run --project .\CloudRunner.WinForms\CloudRunner.WinForms.csproj

7. Click Refresh prerequisites, then Start local viewer. noVNC should ask for the VNC password. The password is not put in the URL or saved by CloudRunner. If noVNC reports an unsupported security type, recheck that TightVNC is set to classical VNC authentication rather than SRP-6a.

## Local ports

| Component | Address | Port |
|---|---|---:|
| TightVNC | 127.0.0.1 | 5900 |
| websockify + noVNC HTTP | 127.0.0.1 | 6080 |

The WebSocket bridge explicitly binds to 127.0.0.1:6080 and forwards only to 127.0.0.1:5900. Do not change either address to 0.0.0.0. The app stops the websockify process when the viewer is stopped or CloudRunner closes. It does not stop or reconfigure TightVNC.

## Important same-machine capture limitation

TightVNC normally shares the host desktop. When the noVNC viewer is displayed on that same desktop, the viewer itself is part of the captured screen and can create a recursive "hall of mirrors" effect. This is a property of whole-desktop capture, not a WebView2 defect.

The first verification can confirm the VNC and WebSocket path, but a clean embedded single-application experience needs application-window capture (if supported by the selected server/version) or a separate viewer machine/session. Do not claim the application-only capture stage is complete until it is tested.

## Security notes

- This is a single-user local prototype, not a public remote desktop service.
- The free TightVNC edition does not encrypt the complete VNC screen/input stream. Keep it on loopback for this prototype.
- Never expose ports 5900 or 6080 to the public internet.
- Do not put VNC credentials in command-line arguments, URLs, source code, or GitHub repository secrets.
- Do not run untrusted downloaded executables as Administrator.
- The current prototype does not download or execute arbitrary EXEs. Process lifecycle and isolated execution are later phases.

## Licensing

- TightVNC: see the official product information at https://www.tightvnc.com/.
- noVNC: MPL-2.0. See https://github.com/novnc/noVNC/blob/master/LICENSE.txt.
- websockify: see https://github.com/novnc/websockify.
- Microsoft WebView2 SDK: see its NuGet package and Microsoft documentation.

## Next milestones

1. Verify local VNC connection and WebView2 rendering.
2. Resolve the same-desktop recursive capture issue with tested window-only capture or a separate viewer session.
3. Add a CloudRunner.HostAgent running in the logged-in user session.
4. Add safe release download, SHA-256 verification, process launch/stop and session cleanup.
5. Evaluate a separately hosted Windows machine only after the local streaming prototype is proven.
