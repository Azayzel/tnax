# TNAX

Native Windows and iPhone photo backup to a private NAS. No hosted application server.

[PLAN.md](PLAN.md) | [DECISIONS.md](DECISIONS.md) | [SHELVED/TODO.md](SHELVED/TODO.md)

## Status

Windows development build runs. The shared core has 44 passing tests, including real loopback HTTPS pairing checks. iPhone C# passes a design-time compile, but a full Mac build, signing, and physical-device tests remain outstanding. This is a development prototype, not a validated family backup.

## Family Setup

1. Receive a NAS setup file from the owner through a trusted channel.
2. Choose **Import NAS setup** in TNAX and confirm the NAS identity. No mkcert commands or system certificate installation are needed on paired clients.
3. Sign in with your own NAS account. Choose a backup folder from the browser if the supplied destination needs changing.
4. On iPhone, grant Photos access and opt into Automatic Photos backup. Windows users add files and start backup.

The setup file contains only the NAS name/address and a public CA certificate. Passwords and private keys are never exported. Importing or removing a pairing clears the saved sign-in but preserves the backup queue.

## Windows

Requires .NET 10, the MAUI Windows workload, and Windows 10 1809 or later.

```powershell
dotnet test tests/Tnax.Core.Tests/Tnax.Core.Tests.csproj
dotnet build src/Tnax.App/Tnax.App.csproj -f net10.0-windows10.0.19041.0 -t:Run
```

Import a NAS setup file, then sign in. Manual HTTPS address entry and owner setup controls are under **Advanced**. The app checks the folder before saving credentials in OS secure storage. Pause retains staged originals; Retry reconciles remote content before another write.

The first-run suggestion is `https://192.168.0.87:474/`; a saved connection always takes precedence. Append the actual WebDAV-enabled shared-folder path before backup. In TOS Shared Folder Settings, enable WebDAV permission and grant the backup account access.

The screenshot shows WebDAV HTTP on 8800 and HTTPS on 474. An unauthenticated OPTIONS request on 8800 advertises DAV 1/2. HTTPS on 474 needs a replacement certificate; see [NAS Certificates](#nas-certificates). TNAX does not downgrade to HTTP or bypass TLS checks.

The TOS administration address `192.168.0.87:8181` is a separate service. Automatic port/share discovery is not implemented; arbitrary custom ports require advertised service discovery or an authenticated NAS API. Do not expose NAS ports publicly.

Authentication currently supports HTTP Basic over HTTPS. Digest-only or proprietary login endpoints are not supported.

## NAS Certificates

Observed on 2026-11-16: the factory WebDAV certificate is self-issued with `CN=webdav`, no hostname/IP SANs, and an expiry in 2040. Windows reports `NotSignatureValid` and `UntrustedRoot`. It is not an expired-certificate or password problem; do not blindly trust this certificate.

These are **owner-only** tasks. Confirm that TOS can assign an imported certificate to WebDAV on port 474. TerraMaster's [current documentation](https://help.terra-master.com/docs/TOS6/controlpanel/general-settings/security#certificate) describes Add/Set under Security > Certificate, but the exact TOS 4.2 workflow remains unverified. Changing only the TOS login certificate may leave WebDAV unchanged.

### Install mkcert

For `mkcert: The term 'mkcert' is not recognized`, install the official portable package for the current Windows user:

```powershell
winget install --id FiloSottile.mkcert --exact --source winget --scope user
$mkcert = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\mkcert.exe'
& $mkcert -version
```

Version 1.4.4 was installed and verified here. The explicit executable path also works when an existing VS Code terminal has stale PATH settings.

### Generate Outside Git

Generate the certificate outside Git. App-local pairing does not require installing the CA in Windows or iPhone system trust stores.

```powershell
$certDir = Join-Path $env:USERPROFILE '.tnax\certificates'
New-Item -ItemType Directory -Path $certDir -Force | Out-Null
& $mkcert -cert-file (Join-Path $certDir 'tnas.crt') -key-file (Join-Path $certDir 'tnas.key') 192.168.0.87 tnas
```

Keep the private keys outside the repository and out of chat. The generated certificate covers both the IP and `tnas`; reserve the NAS address in DHCP if retaining the IP-based URL.

### Assign and Pair

1. Import the generated server certificate and its matching private key into the NAS, then assign them specifically to WebDAV. Retain the previous configuration until the replacement is verified.
2. In TNAX, set the HTTPS destination under Advanced, choose **Share NAS setup**, and select the public `rootCA.pem` from the directory printed by `& $mkcert -CAROOT`.
3. Import that setup file into your own TNAX, then share it with family members through a trusted channel. Each person signs in separately. Never distribute `rootCA-key.pem`; the NAS needs its server key, not the CA key.
4. Test sign-in and folder browsing in TNAX. Pairing validates the certificate chain, hostname, validity dates, and server purpose; it is not an invalid-certificate bypass.

The current NAS still serves the factory certificate even though the replacement was generated successfully. App pairing cannot fix that server-side assignment.

### Optional System Trust

For browsers, PowerShell, or other clients outside TNAX, run `& $mkcert -install` yourself and approve the Windows security prompt directly. For other iPhone clients, install only the public root certificate and enable it under Settings > General > About > Certificate Trust Settings. This broader system trust is not needed for TNAX pairing.

Once system trust is configured, a read-only HTTPS check is:

```powershell
Invoke-WebRequest -Uri 'https://192.168.0.87:474/' -Method Options -MaximumRedirection 0 -TimeoutSec 10 -SkipHttpErrorCheck
```

An HTTP response, including 401/403, means TLS completed; it does not prove folder access. PowerShell does not use TNAX's app-local trust. A public CA certificate is another option with a domain you control instead of this private IP.

## iPhone

On the Mac, install .NET 10, the MAUI iOS workload, and the Xcode version required by that workload. Open this repository and build for the simulator:

```sh
dotnet build src/Tnax.App/Tnax.App.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Use `iossimulator-x64` on an Intel Mac. Register bundle ID `com.azayzel.tnax` with your Apple team before signing a physical-device or TestFlight build. Do not commit signing material.

Import Photos requests access to the native library. Automatic Photos backup is opt-in; its background processing requests require power and network access. Queued uploads use iOS background URLSession. Completed uploads remain unverified until TNAX can read back and hash the remote bytes.

## Behavior

- Copies original resources; never deletes phone or NAS originals. Live Photo components have a JSON manifest recording their relationship and destinations.
- A durable SQLite journal isolates each endpoint/account. Credentials are not stored in that journal.
- Staging is capped at 2 GiB; an individual resource larger than the available budget is rejected. Finish queued work before importing more.
- Each iPhone scan imports up to 24 new assets; each scheduling pass handles up to 48 resources. Large initial libraries need multiple passes.
- Wi-Fi/Ethernet is the default. Cellular requires opt-in. Photos/iCloud download policy still needs physical-device validation.
- Remote access requires a separately configured VPN route to the NAS. TNAX does not install or manage that VPN.
- Windows transfers require the app to remain open. iOS scheduling is opportunistic; reopening is needed after force-quitting.

## Before Real Use

Validate WebDAV conditional writes with a disposable fixture, recover files and Live Photo pairs on a real iPhone, and test interruptions, limited Photos permission, locking, iCloud-only originals, and cellular/VPN transitions. Background URLSession follows redirects automatically: validate the final endpoint and its redirect behavior before trusting it with originals.

Keep existing backups. This does not replicate the entire Apple Photos database or provide an off-site copy.

## Layout

- [src/Tnax.Core](src/Tnax.Core): endpoint checks, WebDAV transfer/verification, durable journal.
- [src/Tnax.App](src/Tnax.App): native MAUI UI, staging, Windows transfers, iOS integration.
- [tests/Tnax.Core.Tests](tests/Tnax.Core.Tests): deterministic protocol, persistence, and integrity tests; no live NAS access.

Native icons are from Lucide 0.468.0; its license is included in [src/Tnax.App/Resources/LUCIDE-LICENSE](src/Tnax.App/Resources/LUCIDE-LICENSE).
