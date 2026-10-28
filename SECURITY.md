# Security

## Protection boundaries

FrameVault encrypts password-protected workspace content inside an MP4 using
AES-256-GCM. Keys are derived from passwords with PBKDF2-HMAC-SHA256 (600,000
iterations and a random 128-bit salt). Each save uses a fresh 96-bit nonce and a
128-bit authentication tag. The password is not stored in the file.

- **Unlocked files are plaintext.** They are extracted into a user-restricted
  working folder. Applications running as your user, administrators, and backup
  or security software may be able to read them.
- Workspaces created without a password contain readable data. Their checksum
  detects accidental corruption; it does not authenticate against an attacker.
- The video is public. Encryption does not hide the presence or size of the
  workspace extension and does not provide pixel-level steganography or plausible deniability.
- Save is manual. Changes are protected in the MP4 only after a successful save.
  A failed save or crash may leave plaintext working files or recovery copies.
- Removing a working folder does not guarantee physical erasure. Editor
  autosaves, thumbnails, backups, paging, and crash dumps are outside this protection.
- Password changes do not update earlier copies or backups. Managed password
  strings cannot be reliably wiped from process memory.
- There is no password recovery. Editing or re-encoding a carrier may destroy its workspace.

FrameVault does not provide a mounted encrypted drive or protection from a
compromised operating system. No independent security audit is claimed.
Read [the user guide](docs/USAGE.md) before storing important data.

## Reporting a vulnerability

Report suspected authentication bypass, data disclosure, unsafe extraction,
path traversal, or data loss privately where possible.

On GitHub, use **Security → Advisories → Report a vulnerability** if private
reporting is enabled for the repository. If no private channel is available,
open an issue asking the maintainer for a private contact method, without
including exploit details or sensitive attachments.

Include the affected version, Windows version, reproducible steps using
synthetic data, expected and actual behavior, and any proposed fix. Never send
your real passwords or private workspace files. Automated tests provide
regression coverage; they are not a substitute for security review.
