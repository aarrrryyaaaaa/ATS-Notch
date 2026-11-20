<p align="center">
  <img src="Assets/logo.png" width="96" height="96" alt="ATS-Notch">
</p>

<h1 align="center">ATS-Notch</h1>

<p align="center">
  Dynamic Island &amp; smart notch companion untuk Windows 10 &amp; 11.
</p>

---

## Fitur Utama

- **Notch & Dynamic Island**: Tampilan idle dengan jam real-time, animasi Optimus Prime, serta bar equalizer audio sistem.
- **Notifikasi Fluid**: Notifikasi aplikasi, Wi-Fi, Internet, Bluetooth, Charger, Caps/Num/Scroll Lock, dan Screenshot dengan efek riak air halus.
- **Media & Audio**: Kontrol media real-time, lirik tersinkronisasi, dan mixer volume per aplikasi.
- **Jadwal Sholat**: Waktu sholat akurat untuk wilayah Indonesia dengan animasi langit parabolik dan hitung mundur waktu berikutnya.
- **Langganan AI**: Integrasi akun Google (Google Gemini &amp; Antigravity), Codex, dan Claude dengan penyimpanan kredensial lokal terenkripsi (Windows DPAPI).
- **Utilitas Harian**: Timer, kalender, baki berkas, cuplikan clipboard, kamera, dan Spotlight.

## Unduh

Unduh installer `.exe` terbaru langsung dari halaman [GitHub Releases](https://github.com/aarrrryyaaaaa/ATS-Notch/releases/latest) (pilih `ATS-Notch-Setup-SelfContained.exe` agar langsung bisa dipasang tanpa install .NET Runtime).

## Kebutuhan Sistem

- Windows 10 (build 19041+) atau Windows 11 (64-bit)
- .NET 8 Desktop Runtime (atau gunakan build *self-contained*)

## Build dari Source

```powershell
dotnet publish ATS-Notch.csproj -c Release -p:SkipNativeCompile=true -p:PublishSingleFile=true -o .\publish
```

---

<p align="center">
  Dikembangkan oleh <a href="https://github.com/aarrrryyaaaaa">aarrrryyaaaaa</a> ·
  <a href="https://github.com/aarrrryyaaaaa/ATS-Notch">GitHub</a>
</p>
