# Payload Format

Payloads are a directory containing `payload.manifest.json` and every listed file. Schema version 1 is:

```json
{
  "schemaVersion": 1,
  "payloadVersion": "2026.09.28",
  "supportedGameBuilds": ["verified-file-version"],
  "files": [
    {
      "name": "MatureData-WindowsClient.pak",
      "sha256": "64 uppercase or lowercase hexadecimal characters",
      "size": 365
    }
  ]
}
```

File names must be base names with no path components, entries must be unique, sizes must match, and every SHA-256 must verify. Schema, application, payload, and game versions are separate concepts.

An empty `supportedGameBuilds` array supports no builds. Unknown builds fail closed. Maintainers must verify a payload against a game build before adding its exact executable file version.

Future remote manifests must be downloaded over HTTPS, validated before use, pinned by a trusted SHA-256 or signature mechanism, and unable to select executable content or escape the payload directory.