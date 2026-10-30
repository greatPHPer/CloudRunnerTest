# CloudRunner.HostAgent

A per-user local process manager for CloudRunner. It is started by the WinForms controller in the logged-in user's session and communicates through a randomly named, current-user-only Windows named pipe.

## Commands

- `start`: download one direct GitHub Release `.exe`, verify its SHA-256, then launch it without elevation.
- `status`: return the process ID and top-level window handle/title when available.
- `stop`: terminate the process tree and remove its temporary job directory.
- `shutdown`: stop the current process and exit the agent.

## Security boundaries

- The initial URL must be HTTPS and match a direct GitHub Release asset path.
- Redirects are limited to GitHub's release-asset hosts over HTTPS.
- Downloads are limited to 100 MB and checked for an MZ executable header.
- SHA-256 must match a caller-supplied 64-character hexadecimal value.
- The process is launched with `UseShellExecute=false` and individual argument entries.
- The agent refuses to launch the downloaded application when the agent itself is elevated as Administrator.
- Named-pipe access is restricted with `PipeOptions.CurrentUserOnly`.
- Only one managed application session is permitted at a time.
- The launched process is assigned to a Windows Job Object with kill-on-close enabled, so the operating system terminates assigned processes if the HostAgent exits unexpectedly.

This is process hygiene, not a security sandbox. Downloaded code runs with the current user's permissions and may access that user's files, credentials, network and other resources. Do not run untrusted executables. A later public product needs additional isolation and a clear trust/consent model.
