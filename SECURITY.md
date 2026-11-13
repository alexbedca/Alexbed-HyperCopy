# Security Policy

## Supported versions
Only the latest release receives fixes. Please update before reporting.

## Reporting a vulnerability
Please **do not** open a public issue for security problems.
Use GitHub's **"Report a vulnerability"** button (Security tab of this repository).
Include the HyperCopy version, your Windows version and steps to reproduce.
I will reply as soon as I can, and credit you in the release notes if you wish.

## Verifying downloads
Each release includes `SHA256SUMS.txt`. In PowerShell:
`Get-FileHash .\AlexbedHyperCopy.exe -Algorithm SHA256`
Releases are not code-signed yet, so this checksum is how you confirm a download is genuine.
HyperCopy does not connect to the internet, needs no admin rights and does not change system settings.
