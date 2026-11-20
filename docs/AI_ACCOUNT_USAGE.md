# ATS-Notch — Langganan AI

ATS-Notch mendukung pemantauan status dan kuota langganan AI secara lokal melalui:

1. **Google AI (Google Gemini & Antigravity)**
   - Login menggunakan akun Google (OAuth 2.0 PKCE di browser) atau deteksi otomatis sesi akun lokal Gemini CLI / Antigravity / Google Cloud CLI.
   - Pilih layanan yang dilanggan (`Google Gemini`, `Antigravity`, atau `Google Gemini & Antigravity`).
   - Semua token dan data akun disimpan secara lokal terenkripsi menggunakan Windows DPAPI di `%LOCALAPPDATA%\ATS-Notch\ai-accounts\` dan tidak pernah dikirim ke repositori GitHub.
2. **Codex & Claude Code**
   - Membaca status dan kuota lokal langsung dari CLI resmi yang terpasang di perangkat Anda.
