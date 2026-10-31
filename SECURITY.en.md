# Security Policy

English | [简体中文](SECURITY.md)

## Reporting a vulnerability

**Do not** open a public issue for a security problem, and do not paste reproducible production credentials, chat transcripts, member records, raw protocol payloads, log excerpts, or screenshots into issues, pull requests, or discussions.

Contact the repository owner privately on GitHub ([@ZhaoJun233](https://github.com/ZhaoJun233)), or use the repository's Security tab "Report a vulnerability" private channel. Please include:

- the affected feature and entry point (which endpoint / command / plugin path);
- trigger conditions and minimal reproduction steps (**synthetic data only**, never live conversations);
- impact (what can be read, changed, or persisted);
- your suggested fix direction (optional).

## Response commitment

This is a personally maintained project. There is **no response-time or fix-time commitment**, and nothing here constitutes an SLA. Reports are triaged and scheduled on a best-effort basis.

## Threat model (read this first)

1. **Plugins are not sandboxed.** A plugin is a .NET assembly loaded inside the host process (`PluginPackageLoader` is a collectible `AssemblyLoadContext` that calls `LoadFromAssemblyPath` and `Activator.CreateInstance` directly). Any enabled plugin can therefore execute arbitrary code and reach everything the host can reach: the database, secrets, logs, and the network. Plugin provenance *is* host trust. Installing or enabling a plugin is a high-privilege action; it is disabled by default and requires an explicit administrator enable plus a restart. "Will this plugin misbehave" is not a question a sandbox answers here — only source review does.
2. **SHA256 proves byte integrity, not code safety.** The manifest hash proves the downloaded package is byte-identical to what the author published; it says nothing about whether those bytes are benign. Neither hashes nor signatures change rule 1.
3. **Permission declarations are disclosure only.** The manifest `permissions` field exists for display and review prompts. The host does not restrict a plugin because it declared few permissions.
4. **Credentials and conversation data are the highest-value targets.** Model API keys, panel tokens, ZBA tokens, and the official-channel app secret live in host process memory and in SQLite; the database file and the `secrets` table sit where the host can read them. Anyone who can execute code in-process — including a malicious plugin — can take them. That is why plugins are disabled by default.
5. **Fail closed by default.** With no ZBA token configured, management endpoints return 503 and only `/zba/v1/health` stays readable; without a panel token the admin write surface is unavailable. If you find an entry point that still works without its token, that is a security defect — report it through the channel above.

## Out of scope

- Vulnerabilities in upstream dependencies (.NET runtime, NuGet packages, NapCat, Caddy, Cloudflare tunnel, the model gateway): report them to those projects. This repository tracks known items through the NuGet audit gate (`NU1903`/`NU1904` as restore errors) and pinned versions.
- Hardening of your own deployment (reverse proxy, firewall, host permissions, backup media custody): that is the deployer's responsibility. The repository only defines the backup and rollback procedure.
- Any conclusion derived from, or exfiltration of, real conversation data: see the privacy red lines in [AGENTS.en.md](AGENTS.en.md).

## Minimum requirements for deployers

- Bind the panel and the protocol endpoint to loopback only, and expose them through a reverse proxy.
- Keep the database file mode 600 on Unix, and treat backups the same.
- Never bake `.env`, the database, or logs into an image or a public snapshot.
- Take a snapshot before upgrading, per [docs/operations/rollback-playbook.md](docs/operations/rollback-playbook.md).
- Enable only plugins from sources you trust.
