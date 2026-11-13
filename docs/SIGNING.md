# Code signing

**Decision (2026-11-12):** the portable executables built by `publish.ps1` ship **unsigned** for now. Revisit if the app gets a wider distribution.

What unsigned means in practice:
- On first run, Windows SmartScreen shows "Windows protected your PC". Users choose **More info → Run anyway**.
- Machines with application-control policies (AppLocker or WDAC) may block unsigned executables. IT can allow them by path or file hash.
- An MSIX installer is not possible without a signature, so the installer waits on this decision too.

## Options when this is revisited

Researched 2026-11-12. Prices are approximate and change; confirm with the vendor before buying.

| Option | Cost (approx.) | Trusted by | Best for | Catches |
|---|---|---|---|---|
| Company's internal certificate authority (AD CS, deployed by Intune or Group Policy) | Free if the company already runs one | That company's managed PCs only | Company-wide rollout of an internal tool; works for MSIX | IT must issue the certificate and push its trust to devices. Meaningless outside the company. |
| Self-signed certificate | Free | Only PCs where it is installed by hand | Testing MSIX packages, small pilots | Manual install on every machine. |
| Microsoft Artifact Signing (formerly Trusted Signing) | Low monthly fee | Publicly trusted | Cheapest publicly trusted option; no hardware token; works with signtool and MSIX | Eligibility is limited by country and identity type (individual onboarding was restricted in 2026). Billing starts when the account is created. |
| OV code-signing certificate (Sectigo, DigiCert, SSL.com, Certum, GlobalSign) | ~$220–$440/yr plus a token or cloud-signing fee | Publicly trusted | Public distribution | Since June 2023 the key must be on a hardware token or cloud HSM. SmartScreen warnings fade as download reputation builds. |
| EV code-signing certificate | ~$290–$620/yr | Publicly trusted | Kernel drivers, or when procurement requires it | Since March 2024 it gives no instant SmartScreen trust, so for this app it costs more without benefit. |
| Microsoft Store (public or private listing) | Developer account: free for individuals, small one-time fee for companies | Microsoft signs the package | Avoiding a certificate; Store handles updates | Store certification review; distribution goes through the Store. |

**Suggested path:** for company-wide use, ask IT for a code-signing certificate from the internal CA and deploy through Intune, which also covers updates. For distribution outside the company, use Artifact Signing if eligible, otherwise an OV certificate with cloud signing.
