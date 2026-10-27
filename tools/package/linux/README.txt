Pennyworth for Linux (x64)

Run it:        bin/pennyworth
Add a launcher (optional, per user):
    cp share/applications/pennyworth.desktop ~/.local/share/applications/
    cp -r share/icons/hicolor ~/.local/share/icons/
    then edit Exec= in the copy to the full path of bin/pennyworth

Nothing else is installed and nothing needs the .NET SDK or administrator rights.

The system supplies:
    required      glibc 2.27 or newer, libstdc++, libicu, libfontconfig, and an X11 or Wayland session
    optional      libsecret-1 (the desktop keyring, for API keys; without it only the keyring is turned off)
                  libssl (HTTPS, for the cloud assistant)
Pennyworth never asks for a password.

PENNYWORTH_DISPLAY=x11 keeps X11 (XWayland) inside a Wayland session.

Licence: LICENSE. Fonts (Golos Text, JetBrains Mono): SIL Open Font License 1.1, built into the app.
