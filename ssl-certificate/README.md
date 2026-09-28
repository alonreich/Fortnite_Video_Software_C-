# ssl-certificate — local code-signing certificate

This folder holds a **code-signing certificate** (Authenticode) for `Build.cmd`. It is not a TLS/HTTPS certificate: the app is a desktop program, so a TLS certificate would not sign anything.

| File | Secret? | Purpose |
|---|---|---|
| `fvs-codesign.pfx` | **yes** | Signing certificate + private key (CN = Alon Reich (FVS local development)), valid 3 years |
| `fvs-codesign.password.txt` | **yes** | Password for the PFX |
| `fvs-dev-root-ca.key.pem` | **yes** | Private key of the local root CA (AES-256, same password). Needed only to issue a new signing certificate |
| `fvs-dev-root-ca.cer` / `.pem` | no | Public certificate of the local root CA |
| `fvs-codesign.pem` | no | Public signing certificate |
| `install-dev-root.cmd` | no | Trusts the local root for YOUR Windows account (run once) |
| `remove-dev-root.cmd` | no | Removes that trust |
| `create-dev-codesign-cert.ps1` | no | Regenerates everything on Windows |

`.gitignore` here keeps every secret file out of git.

## How the build uses it
1. Run `ssl-certificate\install-dev-root.cmd` once and confirm the Windows prompt.
2. Run `Build.cmd`. When `FVS_SIGN_PFX` is not set, the build signs with this certificate, and `signtool verify /pa` passes on this machine.
3. The build **publishes** a release signed with it (your decision, SIGNLOCAL_02). Other computers do not trust this root, so: users DO get the "new version" notification, but in-app install sends them to the release page (the publisher can't be verified on their PCs), and SmartScreen still says "unknown publisher".

## To make in-app updates install automatically
Buy or obtain a **publicly trusted** code-signing certificate, for example a CA-issued OV/EV certificate or Azure Trusted Signing. Then set:

```
set FVS_SIGN_PFX=C:\path\to\real-cert.pfx
set FVS_SIGN_PASS=********
```

Those variables always take precedence over this folder.
