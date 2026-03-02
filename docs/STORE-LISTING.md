# Microsoft Store Listing — QuantumDrive v1.0.0

## App Name
QuantumDrive

## Short Description (max 100 chars)
Post-quantum encrypted virtual drive. Your files, protected by next-generation cryptography.

## Description

QuantumDrive creates an encrypted virtual drive on your PC. Drag files in and out like a normal folder — but everything is transparently encrypted with post-quantum cryptography before it touches disk.

**How it works:**
When you unlock your vault, a drive letter (e.g., Q:\) appears in Windows Explorer. Save files to it just like any other drive. Behind the scenes, every file is encrypted using ML-KEM-1024 (a NIST-approved post-quantum algorithm) and AES-256-GCM. When you lock the vault or close the app, the drive disappears — all that remains are encrypted .qd files that are completely unreadable without your password.

**Key features:**

- Post-quantum encryption (ML-KEM-1024 + AES-256-GCM) — secure against both current and future quantum computers
- Transparent virtual drive — files appear as a normal folder in Explorer
- Zero-knowledge design — your password never leaves your device
- Streaming encryption — constant memory usage regardless of file size
- Multi-vault support — organize files across multiple encrypted vaults
- Recovery key — regain access if you forget your password
- Native Windows integration — files open in apps without Protected View
- No cloud dependency — everything stays on your device
- Free and open

**Privacy:**
QuantumDrive runs entirely on your device. No accounts, no telemetry, no cloud services. Your encryption keys are derived locally from your password and never transmitted anywhere.

**Technical details:**
- ML-KEM-1024 (FIPS 203) for post-quantum key encapsulation
- AES-256-GCM for authenticated file encryption
- Argon2id (64 MB, 3 iterations) for password-based key derivation
- HKDF-SHA256 for file key derivation
- Chunked AEAD with 64 KB blocks for streaming encryption
- Windows Cloud Files API for native drive integration

## Keywords
encryption, encrypted drive, virtual drive, post-quantum, quantum-safe, file encryption, privacy, security, AES-256, ML-KEM, zero-knowledge, vault

## Category
Security

## Age Rating
3+

## Privacy Policy URL
https://quantumdrive.app/privacy

## Support URL
https://quantumdrive.app

---

## Screenshot Guidance

Capture the following screenshots at 1920x1080 (or 2x for HiDPI):

1. **Dashboard — locked vault**: Show the main dashboard with one or more vaults in locked state. Highlights the clean UI and vault management.

2. **Dashboard — unlocked vault with drive mounted**: Show a vault unlocked and the virtual drive toggle on. Highlights the one-click mount experience.

3. **Explorer — Q: drive with files**: Show Windows Explorer with the Q: drive open, displaying files. Highlights that encrypted files look like normal files.

4. **Setup wizard — vault creation**: Show the vault creation step with password field and entropy meter. Highlights the security-first onboarding.

5. **Settings page**: Show the settings page with vault management options. Highlights multi-vault support and password change.

---

## Feature Graphic / Hero Image Guidance

Create a 1920x1080 hero image with:
- App logo (purple circle + white shield) centered or left-aligned
- "QuantumDrive" text in semibold
- Tagline: "Post-quantum encrypted virtual drive"
- Purple gradient background matching the app theme (#7F00FF → #4B0082)
