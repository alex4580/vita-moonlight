## What changed

Describe the user-visible problem and the solution. Say whether this affects
the Vita client, Windows host, installer/uninstaller, display recovery, or
documentation.

## How it was checked

- [ ] `python tools/check-version-consistency.py`
- [ ] `python tools/check-windows-upgrade-contract.py`
- [ ] `python tools/summarize-vita-log.py --self-test`
- [ ] Windows host build and `self-test` (if host code changed)
- [ ] Vita CI build (if Vita code changed)
- [ ] Installer/portable package contents checked (if packaging changed)
- [ ] Documentation and relative links checked

Hardware checks performed:

```text
Vita model/firmware:
Windows build and GPU/driver:
Install state (new / upgrade / same-version repair):
Streaming, input, display-recovery, and uninstall results:
Not tested:
```

## Safety and compatibility

- [ ] Existing Sunshine pairing and unrelated applications are preserved.
- [ ] Failure and disconnect paths restore a physical display.
- [ ] Support data contains no credentials, pairing secrets, private network
      identifiers, typed text, or precise touch/input samples.
- [ ] Shared dependencies are not removed without an explicit user choice.
- [ ] The VPK and Windows packages use the same version and commit.
