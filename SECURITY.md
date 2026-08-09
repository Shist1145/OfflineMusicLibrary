# Security Policy

## Supported versions

| Version | Status |
| --- | --- |
| `1.7.0-preview.1` | Current preview; security and data-loss fixes are accepted |
| `1.6.3` | Current stable Windows release; critical security/data-loss fixes are accepted |
| Older versions | Upgrade recommended |

## Reporting a vulnerability

For a vulnerability that could expose private paths, overwrite/delete files, execute code, leak credentials, or cause reliable resource exhaustion, prefer GitHub's private vulnerability-reporting flow:

<https://github.com/Shist1145/OfflineMusicLibrary/security/advisories/new>

If private reporting is unavailable, open a GitHub issue asking for a private contact channel without including exploit details or personal data. Ordinary non-sensitive bugs can be reported directly through [GitHub Issues](https://github.com/Shist1145/OfflineMusicLibrary/issues).

Please include the affected version, operating system, minimal reproduction, expected impact, and whether the issue requires a crafted media/state/cache file. Do not upload:

- `library-v2.json` or its backups
- real music, lyrics, playlist links, NAS credentials, access tokens, or private file paths
- an unredacted diagnostic log

The project will first confirm receipt and scope, then coordinate a fix and disclosure. No response-time guarantee is promised by this personal project.

## Security model

OfflineMusicLibrary is a local-first desktop application. It does not require a music-account login and does not upload the local library. It does parse local media metadata, lyrics, state JSON, playlist-history exports, local images, and public NetEase playlist responses; these inputs are treated as untrusted and therefore have bounded read/parse paths where practical.

The application is not a sandbox. A process already running as the same Windows user can normally modify that user's application data and media files. Security fixes still prevent crafted cache structures, links, oversized inputs, and remote responses from turning routine application actions into unintended traversal or unbounded resource use.

See [the 1.7.0-preview.1 audit](docs/SECURITY_AUDIT_1.7.0-preview.1.md) for the latest reviewed boundaries.
