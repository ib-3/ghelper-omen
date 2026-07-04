# G-Helper Omen — HP OMEN Port of G-Helper

A lightweight, independent alternative to HP Omen Gaming Hub for HP OMEN (and compatible Victus) laptops. Based on the excellent architecture of [seerge/g-helper](https://github.com/seerge/g-helper), this project replaces Asus-specific ACPI/WMI calls with HP's native WMI BIOS interfaces to provide seamless hardware control without the bloatware.

> [!NOTE]
> **Project Status:** Beta / Community Driven. This is an independent open-source utility and is not affiliated with, authorized, or endorsed by HP Inc. or the original G-Helper project.

---

## 🚀 Why G-Helper Omen?

Official manufacturer software suites are often heavy, resource-intensive, and rely on multiple background services. G-Helper Omen aims to provide the same (or better) tuning capabilities inside a single, lightweight executable that consumes minimal RAM and zero background CPU when idle.

---

## ⚡ Key Features

- **Performance & Thermal Profiles:** Switch between Silent, Balanced, and Turbo modes directly mapped to HP's BIOS thermal policies via WMI.
- **Graphics (GPU) Management:** Toggle between Eco (iGPU only), Standard (Hybrid), Ultimate (discrete), and Optimized auto-switching modes.
- **Custom Fan Curves:** Configure per-mode temperature-to-fan-speed curves for both CPU and GPU with real-time RPM feedback.
- **Advanced Power Tuning:** Fine-tune CPU power limits (Intel PL1/PL2 or AMD SPL/sPPT/fPPT) with smart detection — if limits are BIOS-locked, the sliders automatically gray out so you always know the true state.
- **Live Power Telemetry:** Real-time CPU package power draw displayed via LibreHardwareMonitor with RAPL MSR fallback.
- **Display & Battery Tweaks:** Automatic screen refresh rate switching by power state and configurable battery charge thresholds.
- **Keyboard Backlight Control:** Per-zone RGB adjustments and hotkey bindings.
- **No Unsigned Drivers:** Utilizes PawnIO, a safe signed kernel interface, for all low-level register access — fully compatible with modern Windows Secure Boot policies.

---

## 🖥️ Compatibility

Designed for **HP OMEN** series laptops using the `hpqBIOSInt128` WMI interface (`root\WMI`).

| Component | Support |
|-----------|---------|
| CPU Architecture | Intel (MSR/MMIO PL1/PL2) and AMD (Ryzen SMU SPL/sPPT/fPPT) |
| GPU | NVIDIA (NVAPI) and AMD discrete/integrated |
| Fan Control | HP WMI thermal policy + custom curves |
| Power Limits | Auto-detected from MSR 0x614 — min 150W slider ceiling |
| GPU Mode | Eco / Hybrid / Discrete / Optimized via HP WMI BIOS |
| Secure Boot | ✅ Compatible via PawnIO signed driver |

> Because HP implements slightly different WMI tables across generations, some features may require model-specific adjustments. If a feature behaves unexpectedly on your device, please open an issue with logs and your hardware ID.

---

## 🛠️ Building from Source

Requires: **.NET 8 SDK** and **Visual Studio 2022** (or the `dotnet` CLI) on Windows.

```bash
git clone https://github.com/ib-3/ghelper-omen.git
cd ghelper-omen/app
dotnet publish GHelper.sln --configuration Release --runtime win-x64 -p:PublishSingleFile=true --no-self-contained
```

The compiled standalone executable will be generated in `bin/x64/Release/net8.0-windows/win-x64/publish/`.

---

## ⚙️ Power Limit Detection

G-Helper Omen uses a multi-layer approach to reading and setting CPU power limits:

1. **MMIO (MCHBAR)** — Read/write via PawnIO mapped physical memory (preferred on Intel Meteor Lake+)
2. **MSR 0x610** — Traditional Intel RAPL package power limit register
3. **MSR 0x614** — Used to detect the CPU's actual hardware power ceiling (slider max)
4. **Ryzen SMU** — Used for AMD STAPM/SPL/sPPT/fPPT via the PawnIO kernel interface

If the power limit registers are locked by the BIOS (lock bit set), the sliders in the Fans + Power panel will be grayed out automatically, providing honest feedback about what can actually be changed on your system.

---

## ⚠️ Disclaimer
   
This application allows direct modification of hardware registers, power limits, and cooling policies.

- **Windows Only:** This application relies on Windows Management Instrumentation (WMI) and kernel drivers that are strictly tied to the Windows ecosystem. It will not work on Linux or macOS.
- **Supported Devices:** Designed for HP OMEN and Victus gaming laptops. Standard enterprise machines like EliteBooks, ProBooks, and consumer Envy/Pavilion models use entirely different hardware interfaces and are **not** supported.
- **Use at your own risk.** Incorrect configuration can cause system instability or unexpected shutdowns.
- The developers assume no liability for hardware damage or data loss.
- "HP", "OMEN", and "Victus" are trademarks of HP Inc.

THE SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.

## 💖 Support the Project

If you find this software useful, please consider supporting its development! 
Reverse-engineering undocumented hardware interfaces, debugging kernel drivers, and maintaining compatibility across dozens of OMEN devices takes hundreds of hours.

[☕ **Donate via PayPal**](https://paypal.me/iborbas)

---

## 🤝 Credits & Acknowledgments

| Project | Role |
|---------|------|
| [seerge/g-helper](https://github.com/seerge/g-helper) | Foundational project — UI, layout, and core logic |
| [PawnIO](https://github.com/namazso/PawnIO) | Signed kernel driver for safe MSR/MMIO/SMU access |
| [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | Robust hardware sensor and power telemetry reading |
| [UXTU](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility) | Ryzen SMU undervolting and power limit endpoints |
| [NvAPIWrapper](https://github.com/falahati/NvAPIWrapper) | NVIDIA GPU API access |
| [Linux Kernel](https://github.com/torvalds/linux) | Reference for ACPI/WMI endpoint definitions |

---

### Privacy Policy

This program does not transfer any information to external networked systems.
