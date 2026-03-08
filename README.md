# QuantumDrive

[![CI](https://github.com/GetQuantumDrive/QuantumDrive/actions/workflows/ci.yml/badge.svg)](https://github.com/GetQuantumDrive/QuantumDrive/actions/workflows/ci.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPL%20v3-blue.svg)](LICENSE.md)

Zero-knowledge encrypted virtual drive for Windows. Files are encrypted transparently using post-quantum cryptography before they touch disk.

## How It Works

QuantumDrive mounts a virtual drive (e.g., `Q:\`) using the Windows Cloud Files API (the same technology behind OneDrive). When you save a file to this drive, it's encrypted in real-time and stored as a `.qd` file in your vault directory. When you open a file, it's decrypted on the fly. The plaintext never touches disk.

```
Your App  -->  Q:\ (Virtual Drive)  -->  Cloud Files API  -->  Encrypt  -->  .qd files on disk
```

## Encryption Architecture

### Layer 1: Vault Identity (Argon2id + AES-256-GCM)

When you create a vault, QuantumDrive generates an **ML-KEM-1024** keypair (post-quantum key encapsulation). The private key is encrypted with your master password using:

1. **Argon2id** key derivation (64 MB memory, 3 iterations, 4 lanes) produces a 256-bit master key from your password + random salt
2. **AES-256-GCM** encrypts the ML-KEM private key with the master key

The vault identity file (`vault.identity`) stores:
- Salt (random, 32 bytes)
- Nonce (random, 12 bytes)
- Encrypted ML-KEM private key (AES-256-GCM ciphertext + 16-byte auth tag)
- ML-KEM public key (plaintext, used for encryption without unlocking)
- Recovery key material (separately encrypted copy of the private key)

### Layer 2: File Encryption (ML-KEM-1024 + AES-256-GCM)

Each file is encrypted with a unique key using hybrid post-quantum cryptography:

1. **ML-KEM-1024 Encapsulation**: generates a random shared secret (256 bits) and a 1568-byte capsule. Only the ML-KEM private key holder can recover the shared secret from the capsule.
2. **HKDF-SHA256**: derives the File Encryption Key (FEK) from the shared secret.
3. **AES-256-GCM Encryption**: chunked AEAD with 64 KB chunks and counter nonces. The capsule is bound as AAD on all operations.

### QDRIVE01 File Format

```
+--------------------------------------------------+
| Magic: "QDRIVE01"              (8 bytes)         |
| ML-KEM Capsule                 (1568 bytes)      |
+--------------------------------------------------+
| Metadata Nonce                 (12 bytes)        |
| Metadata Length                (4 bytes, LE)     |
| Metadata Tag                   (16 bytes)        |
| Metadata Ciphertext            (variable)        |
+--------------------------------------------------+
| Data Nonce Prefix              (8 bytes)         |
| Chunk 0: Tag (16) + Ciphertext (≤64 KB)         |
| Chunk 1: Tag (16) + Ciphertext (≤64 KB)         |
| ...                                              |
+--------------------------------------------------+
```

Every file gets a unique nonce and a unique ML-KEM encapsulation, meaning every file has its own encryption key. Compromising one file's key reveals nothing about other files.

### Layer 3: Virtual Drive (Cloud Files API)

QuantumDrive registers as a Windows cloud sync provider using the Cloud Files API (CldApi). Files appear as native NTFS placeholders in Explorer — they look and behave exactly like local files but are transparently decrypted on demand.

- **Read**: when an app opens a placeholder file, the CFAPI callback decrypts the corresponding `.qd` file from the vault and streams the plaintext data
- **Write**: when a file is saved, a FileSystemWatcher detects the change and re-encrypts it back to the vault as a new `.qd` file
- **Delete/Move**: operations are mirrored to the encrypted vault folder

The drive appears as "QuantumDrive" in Explorer's navigation pane with a custom icon. Because files are native NTFS, applications (including Microsoft Office) open them without Protected View restrictions.

### Recovery Key

A 256-bit recovery key is generated during vault creation, encoded as Base32 groups. A separate copy of the ML-KEM private key is encrypted using a key derived from the recovery key (same Argon2id parameters). This allows vault recovery without the master password.

## Tech Stack

- **UI**: WinUI 3 with Mica backdrop, MVVM (CommunityToolkit.Mvvm)
- **Crypto**: BouncyCastle (ML-KEM-1024), .NET AES-GCM, Konscious.Security (Argon2id)
- **Virtual Drive**: Windows Cloud Files API (CldApi) via Vanara.PInvoke
- **Target**: .NET 8, Windows 10 19041+

## Building

```
dotnet build quantum-drive.csproj -p:Platform=x64
dotnet test quantum-drive.Tests/quantum-drive.Tests.csproj -p:Platform=x64
```

## Contributing

Please open an issue before starting significant work so we can discuss the approach. All contributions must be licensed under GPL-3.0.

For security-related issues, see [SECURITY.md](SECURITY.md).

## License

QuantumDrive is licensed under the [GNU General Public License v3.0](LICENSE.md).
