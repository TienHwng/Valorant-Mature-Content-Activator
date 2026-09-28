# Security Policy

## Reporting

Report vulnerabilities privately through GitHub's security-advisory feature. Do not include Riot credentials, access tokens, personal paths, or proprietary game files in a report.

## Scope

Security-sensitive areas include path validation, manifest parsing, archive/download handling, hash verification, backup/restore, rollback, and process-state checks.

The project will not accept process injection, memory or kernel patching, Vanguard/anti-cheat bypasses, credential extraction, signature spoofing, or behavior intended to conceal game modifications.

Remote update implementations must use HTTPS, validate versioned schemas, enforce SHA-256, prevent path traversal, and never execute downloaded payload content.