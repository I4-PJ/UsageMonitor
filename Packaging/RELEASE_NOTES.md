Desktop quota monitoring for your Codex account, with tray/menu-bar status, reset times, saved history, and depletion forecasts.

## Downloads

- **Windows x64:** the `win-x64.zip` asset. Extract the entire folder and start `UsageMonitor.exe`.
- **macOS Apple Silicon:** the `osx-arm64.zip` asset. Extract and move `UsageMonitor.app` to Applications.
- **macOS Intel:** the `osx-x64.zip` asset. Extract and move `UsageMonitor.app` to Applications.
- **SHA256SUMS.txt:** SHA-256 checksums for the three ZIP packages.

All packages include the .NET runtime, MIT license, dependency notices, and installation notes. Codex must be installed and signed in separately. See the [README](https://github.com/I4-PJ/UsageMonitor#readme) for setup and troubleshooting.

## Validation and signing

The release workflow runs unit tests on Windows and both Mac architectures, checks the publish files and Mac bundle metadata, and verifies that the desktop app starts without a Codex account. These startup checks do not validate live account authentication or every dashboard interaction.

The Windows package has no Authenticode signature. Mac packages have no Developer ID signature and are not notarized; macOS may require explicit approval in Privacy & Security. See [Apple's instructions](https://support.apple.com/en-us/102445).

Versions below 1.0 are prereleases. Please report problems with the OS, architecture, Codex version, and reproduction steps.
