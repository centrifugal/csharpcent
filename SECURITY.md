# Security Policy

## Supported versions

Security fixes are released for the latest minor version on NuGet.

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub: use the **"Report a vulnerability"** button in the repository's [Security](https://github.com/centrifugal/csharpcent/security) tab. Reports are visible only to maintainers.

Please do **not** open a public issue for security problems.

Include as much detail as you can:

- a description of the vulnerability
- steps to reproduce
- affected versions and configuration
- potential impact

We will acknowledge the report and assess it promptly. Fixed vulnerabilities are announced in the GitHub release notes and submitted to the [GitHub Advisory Database](https://github.com/advisories), which NuGet reads.

## How we detect vulnerabilities

- `dotnet restore` audits every package against the GitHub Advisory Database, and CI restores in locked mode from the committed lock files.
- Dependabot keeps packages and GitHub Actions current.
- Every GitHub action is pinned to a commit SHA.
- `Centrifugal.Cent` depends on nothing but .NET: cryptography is the platform's, and Ed25519 comes from a library of the application's choice.
