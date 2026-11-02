# CloudRunner Embedded Linux Runtime (QEMU)

This is a WSL-independent Linux guest for CloudRunner. It is built from the
Microsoft WSL2 Linux kernel source, but runs as a normal x86_64 guest under
QEMU. The guest filesystem is produced with Buildroot and includes a small
X11 stack plus `xeyes` and `xterm`.

## Intended runtime layout

The packaged WinForms application will look for:

```
CloudRunner/
  CloudRunner.WinForms.exe
  Agent/
    CloudRunner.HostAgent.exe
  Runtime/
    EmbeddedLinux/
      Qemu/
        bin/
          qemu-system-x86_64.exe
          qemu-img.exe
          *.dll
        share/
          qemu/
      Linux/
        bzImage
        rootfs.ext4
        BUILD-MANIFEST.txt
        LICENSES/
```

The VM is local to the Windows PC. QEMU emulates the x86_64 machine and
presents a virtio VGA display, USB tablet and USB keyboard. The QEMU SDL
window is the first display target for CloudRunner's existing HWND capture
and WebView2 pipeline. QEMU's WHPX accelerator is preferred, with TCG as a
software-emulation fallback.

## Build inputs

- Buildroot release: 2026.08.1
- Linux source: Microsoft/WSL2-Linux-Kernel tag
  `linux-msft-wsl-6.18.58.1`
- Linux configuration: upstream x86_64 defconfig plus
  `linux-qemu.fragment`
- Guest packages: Xorg, xterm and xeyes

The WSL kernel source is used as a source tree only. Its WSL-specific
configuration is not used: the guest is configured for QEMU's PCI, virtio,
display and input devices.

## Build

Run the repository workflow:

`https://github.com/greatPHPer/CloudRunnerTest/actions/workflows/build-embedded-linux-runtime.yml`

It builds the Linux guest under Ubuntu, obtains a Windows QEMU package from
MSYS2, and produces a combined CloudRunner package artifact. The build also
runs Buildroot's `legal-info` target and publishes the build manifest and
license/source materials with the runtime.

## Boot protocol

QEMU loads `bzImage` directly and attaches `rootfs.ext4` as a virtio block
device. The guest init script reads `cloudrunner.app=xeyes` or
`cloudrunner.app=xterm` from `/proc/cmdline`, starts Xorg on display :0,
and launches the requested test application.

## Requirements and limitations

- Windows x64.
- Microsoft Edge WebView2 Runtime for the existing CloudRunner controller.
- WHPX acceleration is optional; QEMU can fall back to TCG, which may be
  considerably slower.
- This first guest image is a test desktop, not yet a general Linux app
  installer. Downloaded ELF/AppImage support, a non-root guest account,
  persistent user data, snapshots, and stronger VM lifecycle controls are
  subsequent milestones.
- QEMU and the guest kernel are open-source components. Redistributed
  binaries must be accompanied by their applicable source, license notices,
  Buildroot legal-info output, and any required offer/source materials.

This is VM isolation, not a hardened malware sandbox. Do not run untrusted
programs until a separate security review and resource/network restrictions
are implemented.
