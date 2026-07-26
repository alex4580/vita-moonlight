# Third-party notices

The Windows installer derives its driver workflow from **DisplayWizard
beta_v0.614** at <https://github.com/PrPlanIT/DisplayWizard>. DisplayWizard is
licensed under GPL-3.0. Its reviewed AutoHotkey source (`.ahk`), upstream README,
and license are shipped in `tools\DisplayWizard`. The legacy GUI executable is
not shipped because its interactive setup path offers an obsolete driver with
an expired private certificate.

The pinned upstream release archive has SHA-256
`7A4F3032D4A30E42BE89F878A0D98710C764F0734DF4F7EE04FF32EA3F6F6B75`.
The packaging script refuses to use an archive with any other digest.

The DisplayWizard folder also includes the officially signed x64 **Virtual
Display Driver 24.12.24** files published with upstream release 25.5.2 at
<https://github.com/VirtualDrivers/Virtual-Display-Driver>. It is MIT licensed
and its license is included. The pinned driver archive has SHA-256
`F93E7CE3D640C83C419B1B72D2C83B2D8E34D83E8B7BA11F3328A40D2DA82FA4`.
Its catalog has a valid SignPath/GlobalSign signature and a DigiCert timestamp.
The companion also verifies each packaged driver-file digest at runtime. It
does not add a private certificate to the Windows trust stores.

The Sunshine driver task also bundles the official, signed **Microsoft Visual
C++ 2015-2022 Redistributable (x64) 14.44.35211.0**, which the upstream driver
lists as a prerequisite. The pinned redistributable has SHA-256
`CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B`.
The packaging script verifies both this digest and the Microsoft publisher
signature. Microsoft publishes the current supported downloads and license
terms at <https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist>.

DisplayWizard itself includes `nefconw.exe`, developed by Nefarius Software
Solutions. Its source is available from <https://github.com/nefarius/nefcon>.
See the notices and licenses in the bundled DisplayWizard distribution and the
linked source repository.

The Windows package also bundles the official, signed **ViGEmBus 1.22.0**
installer from <https://github.com/nefarius/ViGEmBus>. ViGEmBus is licensed
under the BSD 3-Clause License; its license and source URL are included with the
installer. The pinned installer has SHA-256
`89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A`.

For clean-machine setup, the package includes the official signed
**Sunshine v2026.516.143833** AMD64 MSI from
<https://github.com/LizardByte/Sunshine>. Sunshine is GPL-3.0 licensed; its
license and source URL are included. The pinned MSI has SHA-256
`E7208B11A4AB9DD89871133A054BBB8DC55DFBBA408227B0ECCAB22C60B273A2`.
