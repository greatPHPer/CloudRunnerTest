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
- **Experimental WSL-independent Linux guest:** a bundled QEMU x86-64 virtual machine boots a Buildroot root filesystem and a kernel built from Microsoft's WSL2 Linux kernel source. The first guest image includes Xorg, xeyes and xterm. This mode does not require an installed WSL distribution or Ubuntu.
- **Optional WSL compatibility:** downloads and verifies x86-64 ELF/AppImage assets, then launches them inside the user's default WSL 2 distribution using WSLg. Newly created visible desktop windows are passed to the existing capture pipeline.
- **Cloud compute:** none. Windows and Linux applications run locally on the user's PC; WSL is not a remote cloud worker.
- **Remote video streaming:** experimental one-viewer WebRTC prototype; video is sourced from the local preview canvas at about 5 FPS, with manual offer/answer exchange.
- **Remote input:** optional WebRTC data channel for mouse, keyboard shortcuts and typed text. Input is disabled by default and requires both host approval and a viewer-side control toggle. System-key shortcuts (Alt/Windows keys and selected combinations) are blocked.
- **Remote pairing/signalling:** no signalling service yet; SDP is exchanged manually. STUN only; no TURN relay.

## Project layout

    CloudRunnerTest
    ├── .github/workflows
    │   ├── build-cloudrunner-winforms.yml
    │   ├── build-embedded-linux-runtime.yml  # manual QEMU + Linux guest + WinForms package
    │   ├── deploy-cloudrunner-viewer.yml  # manual GitHub Pages deployment
    │   ├── diagnose-gui-session.yml       # old GitHub runner diagnostic experiment
    │   └── run-release-exe.yml            # headless Actions smoke test; not GUI hosting
    ├── viewer
    │   └── index.html                     # standalone browser WebRTC viewer
    ├── CloudRunner.WinForms
    │   ├── CloudRunner.WinForms.csproj
    │   ├── Program.cs
    │   ├── MainForm.cs
    │   ├── HostAgentClient.cs
    │   ├── WindowsCommandLineParser.cs
    │   ├── WindowCaptureService.cs
    │   └── RemoteInputService.cs
    └── CloudRunner.HostAgent
        ├── CloudRunner.HostAgent.csproj
        ├── Program.cs
        ├── AgentProtocol.cs
        ├── AgentServer.cs
        ├── ReleaseAssetDownloader.cs
        ├── LinuxReleaseAssetDownloader.cs
        ├── WslRuntime.cs
        ├── EmbeddedLinuxRuntime.cs
        └── ExecutionSession.cs
    ├── runtime/embedded-linux
    │   ├── README.md
    │   └── buildroot/                 # pinned guest config, kernel fragment and X11 overlay

## Requirements

- Windows 10 version 2004 (build 19041) or later for this build's Windows SDK target and WgcSharp package.
- .NET 10 SDK to build.
- Microsoft Edge WebView2 Evergreen Runtime.
- A Windows 10/11 x64 machine with a compatible Direct3D 11 graphics device for window capture.
- **Bundled Embedded Linux GUI:** Windows x64, QEMU package included in the combined artifact, and a compatible Windows graphics device. WHPX acceleration is preferred when available; QEMU TCG software emulation is the fallback and can be slower. No WSL/Ubuntu installation is required.
- **Optional WSL Linux GUI:** Windows 10 build 19044 or later (or Windows 11), WSL 2, WSLg, and a regular non-root default user in the Linux distribution.
- CloudRunner should be started as a standard, non-elevated Windows user for downloaded application execution.

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

## Experimental WSL-independent Linux GUI execution (QEMU)

The **Embedded Linux** buttons use a bundled x86-64 Linux virtual machine. The
guest kernel is built from Microsoft's open-source WSL2 Linux kernel source,
but it is configured for QEMU's emulated PCI/virtio devices rather than WSL.
Buildroot creates a minimal ext4 filesystem containing Xorg, xeyes and xterm.

This mode is designed to work on a Windows PC with **no WSL installation and
no Ubuntu distribution**. QEMU boots the kernel and guest filesystem from
CloudRunner's `Runtime\\EmbeddedLinux` folder. A per-session qcow2 overlay is
created under the user's LocalAppData CloudRunner Jobs directory, so the
bundled base image is not modified.

### Build the portable package

Run the manual workflow:

https://github.com/greatPHPer/CloudRunnerTest/actions/workflows/build-embedded-linux-runtime.yml

The workflow has three stages: Buildroot compiles the Linux guest on Ubuntu,
MSYS2 supplies the Windows QEMU/SDL binaries, and a Windows runner publishes
the combined WinForms + runtime package as
`CloudRunner-EmbeddedLinux-<run number>`.

Extract the complete artifact while preserving its folder structure. The
expected layout is:

```
CloudRunner/
  CloudRunner.WinForms.exe
  Agent/
  Runtime/
    EmbeddedLinux/
      Qemu/bin/
      Qemu/share/qemu/
      Linux/bzImage
      Linux/rootfs.ext4
      Linux/LICENSES/
```

### Test xeyes and xterm

1. Start CloudRunner as a standard, non-elevated Windows user.
2. Under **RUN BUNDLED LINUX APPLICATION (NO WSL)**, click **Launch xeyes**
   or **Launch xterm**.
3. Wait for the QEMU window and guest desktop to appear. The guest application
   can take several seconds to start after the QEMU window opens.
4. Click **Capture application window** or **Start live preview** to show the
   QEMU display in WebView2.
5. Stop the live preview, then click **Stop application** to terminate QEMU and
   remove the per-session writable overlay.

The first milestone deliberately launches only the bundled xeyes and xterm
test applications. It does not yet install arbitrary Linux packages or launch
user-supplied ELF/AppImage files. Those require a guest-side application
staging protocol, dependency handling, and a non-root guest account.

WHPX is used when Windows Hypervisor Platform acceleration is available.
QEMU can fall back to TCG software emulation, which may be significantly
slower. The virtual machine runs locally; it is not a remote cloud worker.
This VM is a stronger isolation boundary than launching Linux programs in
the host WSL distribution, but it is not yet a hardened malware sandbox.

## Experimental Linux GUI execution through WSLg

The WSL Linux runtime prototype adds a Linux section to the WinForms client. It does not require a Linux cloud VM: the Linux program runs in the user's own WSL 2 distribution on the same Windows PC.

### One-time Windows setup

1. Check the Windows version with winver. WSLg GUI applications require Windows 10 build 19044 or later, or Windows 11.
2. Open PowerShell as Administrator and run:

       wsl --install -d Ubuntu

3. Restart Windows if requested. Launch Ubuntu from the Start menu and create a regular Linux username and password. Do not configure the default WSL user as root.
4. In PowerShell, run:

       wsl --update
       wsl --shutdown
       wsl --list --verbose

   Confirm that the Ubuntu distribution uses WSL version 2. Start Ubuntu once after the shutdown command so it can initialize again.

Microsoft's WSLg documentation: https://learn.microsoft.com/en-us/windows/wsl/tutorials/gui-apps

### Test WSLg window capture

1. Open Ubuntu from the Start menu and run:

       sudo apt update
       sudo apt install -y x11-apps

2. Start CloudRunner as a standard, non-elevated Windows user.
3. Scroll to **RUN A LINUX GUI APPLICATION (WSL2)** and click **Check WSL2 / WSLg readiness**.
4. Click **Launch WSLg interactive test (xterm)**. If xterm is missing, CloudRunner tells you to install it with `sudo apt update && sudo apt install -y xterm` inside Ubuntu.
5. Click **Start live preview**. The xterm terminal should appear inside WebView2.
6. Use the remote viewer's **Send text** box (or enable mouse + keyboard control and click the preview) to verify input. The host must check **Allow this connected viewer to control the application** first.
7. Click **Stop application** to terminate the Linux process group.

### Run a Linux asset

1. Start CloudRunner as a standard, non-elevated Windows user.
2. Scroll to **RUN A LINUX GUI APPLICATION (WSL2)**.
3. Click **Check WSL2 / WSLg readiness**. The status must report a distribution, a non-root Linux UID, and a GUI display.
4. Paste a direct GitHub Release asset URL for an x86-64 Linux ELF executable or x86-64 AppImage.
5. Enter the publisher-provided SHA-256 and optional Linux arguments.
6. Click **Download, verify and run Linux app**. CloudRunner downloads to its local per-session folder, verifies the ELF architecture and SHA-256, copies the file into the WSL user's Linux filesystem (ext4), marks it executable there, and launches it in a separate Linux process group. The temporary Linux-side folder is removed when the session is stopped or its launcher exits.
7. CloudRunner looks for the new desktop window and uses the existing one-frame / continuous preview buttons to display it in WebView2.
8. Click **Stop application** to stop the Linux process group and remove the temporary download folder.

This first milestone supports **x86-64 ELF, AppImage, .bin, .elf and .run assets** from direct HTTPS GitHub Release URLs, up to 100 MB. It is not a general-purpose Linux package manager: .deb, .rpm, tar archives, ARM binaries, and dependency installation are not supported yet. AppImages are launched with extract-and-run enabled, but some still require libraries installed in the Ubuntu distribution.

This is a local compatibility prototype, not remote execution. WSLg displays Linux GUI windows on the Windows desktop; CloudRunner then captures the corresponding Windows desktop window. Some Linux apps may not create a capturable top-level window, may launch child processes that outlive their launcher, or may require additional dependencies. Those cases need compatibility work.

## Execution security

CloudRunner is a launcher, not yet a hardened malware sandbox. Windows release EXEs run with the current Windows user's permissions. The optional WSL backend runs Linux programs with the default WSL user's permissions. The bundled QEMU backend runs a guest OS in a VM, but it has not yet undergone a dedicated adversarial isolation review. A matching checksum verifies bytes, not software safety.

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
- The WSL Linux prototype accepts only direct HTTPS GitHub Release URLs and checks the ELF file type, x86-64 architecture, maximum size and expected SHA-256 before launch.

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

### Mobile viewing diagnostics

The browser viewer now samples WebRTC statistics every 2.5 seconds. It reports received and decoded video frames, bytes received, packet loss, video element playback state, page visibility, and selected candidate-pair byte counters. The host preview similarly reports encoded/sent frames and outbound bytes. The reports omit IP addresses.

On Android, use **Keep screen awake** during a viewing test and keep Chrome in the foreground. The Screen Wake Lock is optional, requires a user tap, and is released when the viewer disconnects. If the phone locks or Chrome is backgrounded, page visibility diagnostics help identify that transition.

A WebRTC `disconnected` state can be temporary. Keep the session open for recovery; do not treat it as the same as `failed` or `closed`. Compare successive statistics: if host `framesEncoded/bytesSent` continue increasing while Android `framesReceived/bytesReceived` stop, investigate the network path; if sender frames stop too, investigate host capture or WebView2 canvas output; if bytes and frames continue but video time stops, investigate Android video playback/rendering.

### WebRTC connection troubleshooting

The manual host and viewer exchange now tries both Google's STUN endpoint and Cloudflare's public STUN endpoint. STUN discovers candidate addresses; it does not relay video. A browser error **701** means that ICE could not reach the named STUN/TURN server from that ICE agent. It does not, by itself, prove that Windows Firewall is blocking the peer connection.

If the peers show zero candidate pairs, check that both devices are on the same non-guest Wi-Fi, that the router does not have AP/client isolation enabled, and that VPN/security software is not blocking local or UDP traffic. The host's WebRTC diagnostics are shown in a copyable text box; the viewer has a copyable ICE diagnostics panel. Both omit IP addresses.

### Network limitations

The prototype configures a public STUN server and uses non-trickle ICE by exchanging the complete offer and answer after candidate gathering. It does not include a TURN server. Direct peer-to-peer media works only when the two networks can establish a usable ICE candidate pair; restrictive NATs, VPNs, corporate firewalls, or blocked UDP may prevent connection. A TURN relay may be required for those networks and is not included in the zero-budget prototype. Test on the same LAN first, then on separate networks.

This is an experimental demonstration, not a hardened remote-access product. Treat the SDP exchange as a temporary viewing invitation and end the session when finished.

## Zero-cost public distribution

CloudRunner itself can be distributed through GitHub Releases. Every public user runs the app on their own Windows PC. CloudRunner does not consume your work laptop or any central Windows VM.

The experimental viewer already uses direct WebRTC peer-to-peer video when network topology permits. A TURN relay may still be needed for some NAT/firewall combinations; those sessions cannot be guaranteed to work on a strict zero-budget plan. GitHub Pages serves only the static viewer page, not the video.

## Third-party components

- Microsoft WebView2 SDK: Microsoft package and runtime.
- WgcSharp 1.1.1: MIT licensed; see [Third-party notices](THIRD-PARTY-NOTICES.md).
- Windows Graphics Capture: Windows platform API.

## Development roadmap

1. **Phase 1:** local Windows HostAgent, verified EXE download and process lifecycle.
2. **Phase 2:** continuous local window-frame capture and WebView2 rendering.
3. **Phase 3:** experimental video-only WebRTC peer-to-peer prototype.
4. **Phase 4:** WSL2/WSLg Linux ELF and AppImage compatibility prototype; dependency and GUI-window compatibility testing remains.
5. **Phase 5:** signalling/pairing, viewer authentication, TURN strategy, quotas and abuse controls; remote input remains disabled until a separate security review.
6. **Phase 6:** macOS compatibility research (Darling / Darwin_Computa are experimental and are not integrated yet).
7. **Phase 7:** review hardening, installer, public documentation and publisher trust/safety workflow.
