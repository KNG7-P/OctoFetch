# MitmCore — binaries for the optional TLS-bypass engine

This folder is read by `OctoFetch.Services.MitmService`. It needs to contain
the four xray-core artefacts below for the MITM engine to work. They're
copied to the build output via the `<None Update="MitmCore\**\*">` entry in
`OctoFetch.csproj`.

| File                          | What it is                                              | Where it comes from                       |
|-------------------------------|----------------------------------------------------------|--------------------------------------------|
| `xray.exe`                    | Main proxy core                                          | https://github.com/XTLS/Xray-core/releases |
| `wintun.dll`                  | Optional WinTun driver (for advanced routing)            | bundled with xray-core Windows builds      |
| `geosite.dat`                 | Domain → geosite tag database (used by the JSON config)  | https://github.com/v2fly/domain-list-community |
| `geoip.dat`                   | IP → country DB (used by the JSON config)                | https://github.com/v2fly/geoip             |

The default JSON config (`MITM-DomainFronting.json`) is embedded in the
assembly and dropped into this folder on first run if it doesn't already
exist. Users can edit it from the in-app **MITM engine** settings dialog.

Generated artefacts that the engine creates at runtime (and that should NOT
be committed):
- `mycert.crt`, `mycert.key` — the local MITM root CA + private key.
  Regenerated automatically the next time the engine starts if they're
  missing.

If the binaries are missing, the engine silently falls back to direct
connections — YouTube search and Drive OAuth will still attempt to reach
Google directly, which works on unrestricted networks but may fail in
regions where Google is blocked.
