# Third-party notices

## WgcSharp 1.1.1

CloudRunner uses the WgcSharp NuGet package (version 1.1.1) for Windows Graphics Capture window preview.

Project: https://github.com/Giltanas/WgcSharp  
NuGet: https://www.nuget.org/packages/WgcSharp/1.1.1  
License: MIT

Copyright (c) 2026 Giltanas

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

WgcSharp declares dependencies on Vortice.Direct3D11, System.Drawing.Common and Microsoft.Extensions.Logging.Abstractions. Their package licenses and notices remain applicable and should be included with a redistributed installer according to their respective package terms.

## Embedded Linux guest

The optional WSL-independent guest image is assembled with these upstream
components. The combined runtime workflow collects Buildroot's
`output/legal-info` into `Runtime/EmbeddedLinux/Linux/LICENSES` and includes
the QEMU package/source metadata and license directory in the QEMU runtime.

### Linux kernel

Source: https://github.com/microsoft/WSL2-Linux-Kernel  
Pinned tag: `linux-msft-wsl-6.18.58.1`  
License: GPL-2.0 (see the kernel source tree's `COPYING`, `LICENSES/` and
`Documentation/process/license-rules.rst`).

The kernel is configured for QEMU x86-64/virtio hardware. The WSL-specific
kernel configuration is not used.

### Buildroot

Source: https://github.com/buildroot/buildroot  
Pinned release: `2026.08.1`  
License: GPL-2.0 (Buildroot build system). The exact Buildroot legal-info
output is included with the guest artifact.

### QEMU and MSYS2 runtime

QEMU source: https://www.qemu.org/  
QEMU Windows packages: https://packages.msys2.org/  
MSYS2 package recipes: https://github.com/msys2/MINGW-packages/tree/master/mingw-w64-qemu  
QEMU license: GPL-2.0-or-later for the main system emulator, with additional
component-specific licenses. See the included upstream QEMU source archive,
MSYS2 package metadata, and license files.

The QEMU package uses dynamically linked MSYS2 UCRT64 libraries. Their
individual licenses remain applicable. The package artifact includes the
MSYS2 installed-package manifest and available MSYS2 license texts; check the
package/source metadata before redistributing the binaries outside the
development workflow.

**Redistribution note:** the guest image and QEMU package are experimental
build outputs. Before publishing a public installer, verify that the exact
source and license materials for every redistributed kernel, Buildroot,
QEMU, and MSYS2 dependency are included or made available in the form
required by each applicable license.
