# Privacy policy

Last updated: July 26, 2026

This policy covers the Vita Moonlight client and the Vita Moonlight Host
companion maintained at
<https://github.com/alex4580/vita-moonlight>.

## Summary

**This program will not transfer any information to other networked systems
unless specifically requested by the user or the person installing or
operating it.**

Vita Moonlight has no project-operated account service, telemetry, analytics,
advertising, automatic crash upload, or automatic support-log upload. The
maintainer receives no runtime data unless a user deliberately submits it,
for example in a GitHub issue.

Vita Moonlight is a network-streaming application. Starting and operating it
requests the local discovery and user-selected host communication described
below.

## Network activity

The Vita client can:

- use local-network discovery to find compatible streaming hosts and check
  the availability of saved hosts while the app is open;
- connect to a host that the user selects or manually enters;
- exchange pairing data with that host after the user initiates pairing;
- receive video and audio and send controller, touch, keyboard, and motion
  input during a stream;
- send a Wake-on-LAN packet only when the user chooses the wake action; and
- connect through a routed, VPN, or internet path when the user configures
  and selects such a host.

This traffic is between systems selected or operated by the user. Vita
Moonlight does not redirect it through a server operated by this project.

The Windows companion configures a self-hosted streaming service. The
recommended package includes
[Sunshine](https://github.com/LizardByte/Sunshine), whose web interface,
pairing, network listeners, logs, and optional network exposure are governed
by Sunshine's
[documentation](https://docs.lizardbyte.dev/projects/sunshine/latest/).
Users who select an existing
[Apollo](https://github.com/ClassicOldSong/Apollo) installation should review
that project's documentation and configuration as well. Other games,
launchers, VPNs, and network services chosen by the user have their own
privacy practices.

## Information stored locally

Depending on the features used, Vita Moonlight stores information locally on
the Vita or Windows PC, including:

- app settings, saved host names and addresses, pairing material, and button
  or touch mappings;
- Windows host settings, display-recovery state, and the records needed to
  restore Vita-owned Sunshine configuration;
- credentials and pairing state maintained by the selected Sunshine or Apollo
  installation; and
- optional support files that the user explicitly creates.

Vita support logging is off by default. When started by the user, it writes a
short structured file locally and does not upload it. The Windows support
report is also created only when requested and saved to a location chosen by
the user. See [Logging and support](docs/LOGGING_AND_SUPPORT.md) for contents,
paths, rotation, and redaction guidance.

Uninstalling the Windows host removes Vita-owned host settings and recovery
integration as described in the [uninstall guide](README.md#uninstall).
Shared components are kept by default because other software may use them.
Deleting the Vita app may leave external settings or support files so they can
survive a reinstall; users can review or remove those files with VitaShell.

## Information a user chooses to share

Bug reports, support logs, support reports, screenshots, and videos are never
sent automatically. If a user uploads them to GitHub or another community
service, that service's privacy policy applies. Review every attachment and
remove credentials, pairing data, tokens, usernames, host names, personal
paths, IP/MAC addresses, and unrelated content before sharing it.

GitHub's handling of voluntarily submitted repository content is described in
the [GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

## Build and signing services

GitHub Actions processes source code, build metadata, and release artifacts
for maintainers; this is separate from end-user runtime data. If a release is
signed through SignPath, SignPath processes the release artifact and signing
request metadata under the
[SignPath privacy policy](https://signpath.io/privacy-policy). Vita Moonlight
does not send an end user's stream, input, settings, or support files to
SignPath.

## Questions and changes

Ask a non-sensitive privacy question or report an inaccurate disclosure
through [GitHub issues](https://github.com/alex4580/vita-moonlight/issues).
Do not publish credentials or security-sensitive personal information.

This policy will be updated when project-controlled collection, storage,
network transfer, bundled service behavior, or support-report behavior
changes.
