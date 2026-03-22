# QuantumDrive License File

## Free Tier

QuantumDrive is free for personal use with 1 vault (any storage backend). No license file is
required.

**Important:** The free tier is licensed for personal, non-commercial use only. If you are using
QuantumDrive in a business or commercial context, you need a Pro Business license.

## Pro License

A Pro license removes the 1-vault limit and authorizes commercial use.

| Tier | Vaults | Use case |
|------|--------|----------|
| Pro Individual | Unlimited | Personal power users |
| Pro Business | Unlimited | Commercial / business use |

Purchase a license at **https://quantumdrive.app/pro**.

## Installing Your License

After purchase you will receive a `license.qdlic` file. Place it at:

```
%LOCALAPPDATA%\QuantumDrive\license.qdlic
```

Restart QuantumDrive. The vault limit will be lifted immediately.

## License File Format

The license file is a JSON document signed with Ed25519:

```json
{
  "email": "user@example.com",
  "tier": "pro_individual",
  "issuedAt": "2025-01-01T00:00:00Z",
  "expiresAt": null,
  "sig": "<base64-encoded Ed25519 signature>"
}
```

`tier` is one of `"pro_individual"` or `"pro_business"`.
`expiresAt` is `null` for perpetual licenses or an ISO 8601 date for subscription licenses.
The signature covers the payload `"{email}|{issuedAt}|{expiresAt}"`.
