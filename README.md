# QuantumDrive

Zero-knowledge encrypted virtual drive for Windows. Files are encrypted transparently using post-quantum cryptography before they touch disk.

## How It Works

QuantumDrive mounts a virtual drive (e.g., `Q:\`) via a local WebDAV server. When you save a file to this drive, it's encrypted in real-time and stored as a `.qd` file in your vault directory. When you open a file, it's decrypted on the fly. The plaintext never touches disk.

```
Your App  -->  Q:\ (Virtual Drive)  -->  WebDAV Server (localhost)  -->  Encrypt  -->  .qd files on disk
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
2. **AES-256-GCM Encryption**: the shared secret is used as the File Encryption Key (FEK). A random 12-byte nonce is generated per file. The file data is encrypted with AES-256-GCM using this FEK and nonce.

### QDRIVE11 File Format

```
+--------------------------------------------------+
| Magic: "QDRIVE11"              (8 bytes)         |
| Metadata Length                 (4 bytes, LE)     |
| Metadata JSON (name, size, hash, timestamp)       |
+--------------------------------------------------+
| Nonce                           (12 bytes)        |
| ML-KEM-1024 Capsule            (1568 bytes)       |
| AES-GCM Auth Tag               (16 bytes)         |
| Ciphertext                     (variable)         |
+--------------------------------------------------+
```

Every file gets a unique nonce and a unique ML-KEM encapsulation, meaning every file has its own encryption key. Compromising one file's key reveals nothing about other files.

### Layer 3: Virtual Drive (WebDAV over Loopback)

A Kestrel-based WebDAV server runs on `localhost:{random_port}`. Windows maps a drive letter to this server. The WebDAV handler intercepts all file operations:

- **PUT** (write): encrypts the file with ML-KEM + AES-256-GCM, writes `.qd` to vault
- **GET** (read): reads `.qd` from vault, decapsulates ML-KEM, decrypts with AES-256-GCM
- **DELETE/MOVE**: operates on the encrypted `.qd` files

The drive appears as "QuantumDrive" in Explorer's navigation pane with a custom icon.

### Recovery Key

A 256-bit recovery key is generated during vault creation, encoded as Base32 groups. A separate copy of the ML-KEM private key is encrypted using a key derived from the recovery key (same Argon2id parameters). This allows vault recovery without the master password.

## Licensing

License keys are Ed25519-signed. The app contains only the public key and can verify but not generate keys. The generator tool (`tools/generate-license.ps1`) holds the private key.

Key format: `QDPRO-XXXXX-XXXXX-...` (Base32-encoded 4-byte serial + 64-byte Ed25519 signature)

## Tech Stack

- **UI**: WinUI 3 with Mica backdrop, MVVM (CommunityToolkit.Mvvm)
- **Crypto**: BouncyCastle (ML-KEM-1024, Ed25519), .NET AES-GCM, Konscious.Security (Argon2id)
- **Virtual Drive**: ASP.NET Core Kestrel (WebDAV server), loopback-only
- **Target**: .NET 8, Windows 10 19041+

## Building

```
dotnet build quantum-drive.csproj -p:Platform=x64
```

## Generating License Keys

```powershell
.\tools\generate-license.ps1 -Count 5
```
