# Vita Moonlight mDNS discovery

Despite the historical directory name, this is first-party Vita Moonlight
code. It replaces the former `AorsiniYT/mdnsniff` git submodule, whose README
did not provide an unambiguous standard open-source license.

The parser and Vita socket adapter were independently implemented from the DNS
wire format and DNS-SD service model. They are licensed with the rest of Vita
Moonlight under `GPL-3.0-only`. The parser is deliberately platform-neutral so
malformed-packet, compression-pointer, state-correlation, and duplicate-result
behavior can be tested natively before a VPK is built.
