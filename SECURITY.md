# Security Policy

Nyx performs irreversible filesystem operations. Treat unexpected deletion, target-validation bypasses, link traversal, identity races, incomplete overwrite reporting, and exposure of report paths as security-sensitive defects.

## Supported versions

Nyx has not published its first beta. Once `v0.1.0-beta.1` is released, only the newest published beta will receive security fixes; development snapshots are unsupported.

## Reporting a vulnerability

The repository owner and remote have not been configured, so there is currently no verified private reporting channel. A public release must not be made until this section names a working private route, preferably GitHub private vulnerability reporting.

Until then, do not post exploit details, private file paths, reports containing paths, or sample sensitive data in a public issue. Contact the repository owner through an already trusted private channel and include:

- the Nyx version and Windows build;
- filesystem and storage type;
- minimal reproduction steps using disposable test data;
- expected and observed target/result states;
- whether any non-target object was modified;
- relevant Win32 error codes, with personal paths redacted.

Never reproduce a suspected bug against real user data. Use an isolated, disposable test directory or volume and preserve a canary outside the selected target.

## Security expectations

- Nyx runs as the current standard user and does not bypass Windows access controls.
- Nyx does not provide a remote service, account system, updater, telemetry, or network feature in v0.1.
- Security reports must be evaluated against the documented [threat model](docs/threat-model.md); limitations explicitly excluded there are not automatically vulnerabilities.
- A result-labeling error is security relevant when it could lead a user to believe an overwrite or deletion succeeded when it did not.
