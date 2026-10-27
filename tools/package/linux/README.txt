Pennyworth for Linux (x64)

Run it:        bin/pennyworth
Add a launcher (optional, per user):
    cp share/applications/pennyworth.desktop ~/.local/share/applications/
    cp -r share/icons/hicolor ~/.local/share/icons/
    then edit Exec= in the copy to the full path of bin/pennyworth

Nothing needs the .NET SDK, and the app itself needs no administrator rights: it runs as you and installs nothing.

The system supplies:
    required      glibc 2.27 or newer, libstdc++, libicu, libfontconfig, and an X11 or Wayland session
    optional      libsecret-1 (the desktop keyring, for API keys; without it only the keyring is turned off)
                  libssl (HTTPS, for the cloud assistant)
                  polkit (pkexec) and an authentication agent, for the four administrator buttons below

Passwords
    Pennyworth never asks for a password itself and never sees it. On Linux it asks the system for administrator
    rights (polkit, the same prompt as any other program) only when YOU press one of four buttons:
        - memory modules: reads the memory information from the computer's firmware (read-only)
        - full system space: measures snapshots and file system metadata (read-only)
        - measure folders you cannot open: reads only sizes, so Storage adds up (read-only)
        - clean system package caches / crash reports: deletes ONLY the files listed in the preview you confirmed
    Nothing else is changed. If you decline or the prompt is not available, the app carries on without that number.

The optional helper (install-helper.sh)
    The first two buttons also work without anything installed (the system's own tools run through pkexec).
    Cleaning root-owned folders and measuring folders you cannot open need the helper, a small program that runs as
    root only when you press those buttons. This tarball ships it; install it yourself, once, with:
        sudo ./install-helper.sh
    It copies two files, both owned by root: /usr/libexec/pennyworth/pennyworth-helper and
    /usr/share/polkit-1/actions/app.pennyworth.policy. Remove them with:  sudo ./install-helper.sh --uninstall
    Pennyworth runs the helper only from that path, and only if the file and every folder above it are owned by root,
    not writable by group or others and not symbolic links. The AppImage does not contain the helper; one installed
    from this tarball is used by any build of the app. Nothing installs it for you.

PENNYWORTH_DISPLAY=x11 keeps X11 (XWayland) inside a Wayland session.

Licence: LICENSE. Fonts (Golos Text, JetBrains Mono): SIL Open Font License 1.1, built into the app.
