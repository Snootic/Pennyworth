#!/usr/bin/env bash
# Installs (or removes) Pennyworth's optional administrator helper and its polkit policy.
#
# Usage: sudo ./install-helper.sh [--helper PATH] [--policy PATH]
#        sudo ./install-helper.sh --uninstall
#
#   --helper PATH   the helper binary to install (default: libexec/pennyworth/pennyworth-helper or pennyworth-helper next to this script)
#   --policy PATH   the polkit policy to install (default: share/polkit-1/actions/app.pennyworth.policy or app.pennyworth.policy next to this script)
#   --uninstall     remove exactly the two installed files, and /usr/libexec/pennyworth if that leaves it empty
#   -h, --help      this text
#
# What it installs, both owned by root:
#   /usr/libexec/pennyworth/pennyworth-helper              0755   the only program of Pennyworth that runs as root
#   /usr/share/polkit-1/actions/app.pennyworth.policy      0644   one polkit action per helper subcommand
#
# Pennyworth runs the helper only from that path, only through pkexec (your administrator password is asked by the
# system, never by Pennyworth), and only if the file and every folder above it are owned by root, not writable by
# group or others and not symbolic links. This script refuses to install into a path that does not meet that rule.
# Running it again replaces the files (atomically) or reports that they are already current.
# Nothing runs this script for you: not the app, not the package build.
#
# For packagers and tests: DESTDIR=/some/dir stages everything below that directory instead of the real system (the
# policy still names the real helper path). Only the folders below DESTDIR are checked. Without root the script
# refuses, except when both DESTDIR and PENNYWORTH_INSTALL_TEST=1 are set: then it runs as the calling user, does not
# set owners and does not require root-owned folders. That is for trying the script in a temporary directory only.
set -euo pipefail
umask 022

helper_dir=/usr/libexec/pennyworth
helper_dest="$helper_dir/pennyworth-helper"
policy_dir=/usr/share/polkit-1/actions
policy_dest="$policy_dir/app.pennyworth.policy"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
helper_src=""
policy_src=""
uninstall=false
destdir="${DESTDIR:-}"
destdir="${destdir%/}"
test_mode=false

die() { echo "install-helper.sh: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --helper) helper_src="${2:?--helper needs a path}"; shift 2 ;;
        --policy) policy_src="${2:?--policy needs a path}"; shift 2 ;;
        --uninstall) uninstall=true; shift ;;
        -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "install-helper.sh: unknown option: $1 (see --help)" >&2; exit 2 ;;
    esac
done

if [ -n "$destdir" ]; then
    case "$destdir" in /*) ;; *) die "DESTDIR must be an absolute path" ;; esac
fi

if [ "$(id -u)" -ne 0 ]; then
    if [ -n "$destdir" ] && [ "${PENNYWORTH_INSTALL_TEST:-}" = 1 ]; then
        test_mode=true
    else
        die "this changes system files and must run as root. Run it with sudo:  sudo $0 $*"
    fi
fi

# A folder of the destination chain is trusted when it is a real directory (not a link), owned by root and not
# writable by group or others: the app applies the same rule before it will run the helper.
check_dir() { # path
    local path="$1" info
    [ ! -L "$path" ] || die "refusing: $path is a symbolic link"
    [ -d "$path" ] || die "refusing: $path is not a directory"
    info="$(stat -c '%u %A' "$path")"
    if ! $test_mode && [ "${info%% *}" != 0 ]; then die "refusing: $path is not owned by root"; fi
    local mode="${info#* }"
    [ "${mode:5:1}" != w ] && [ "${mode:8:1}" != w ] || die "refusing: $path is writable by group or others"
}

# Checks every folder of $1 (a real-system path) that exists, from "/" (or from DESTDIR) down; what does not exist yet is
# created later by this script as root:root 0755.
check_chain() { # directory
    local part cur="$destdir"
    local -a parts
    if [ -z "$destdir" ]; then check_dir /; fi
    IFS=/ read -ra parts <<<"${1#/}"
    for part in "${parts[@]}"; do
        cur="$cur/$part"
        if [ ! -e "$cur" ] && [ ! -L "$cur" ]; then return 0; fi
        check_dir "$cur"
    done
}

check_target() { # file the script is about to replace or remove
    if [ -L "$1" ]; then die "refusing: $1 is a symbolic link"; fi
    if [ -e "$1" ] && [ ! -f "$1" ]; then die "refusing: $1 exists and is not a regular file"; fi
    return 0
}

is_elf() { [ "$(head -c 4 "$1" | od -An -tx1 | tr -d ' \n')" = 7f454c46 ]; }

check_helper_file() { # path
    [ -f "$1" ] && [ ! -L "$1" ] || die "the helper $1 is not a regular file"
    [ -x "$1" ] || die "the helper $1 is not executable"
    is_elf "$1" || die "the helper $1 is not an ELF executable (publish src/Pennyworth.Helper with Native AOT, see tools/package/build-linux.sh)"
}

owner_args=(-o root -g root)
if $test_mode; then owner_args=(); fi

if $uninstall; then
    removed=0
    for entry in "$policy_dir|$policy_dest" "$helper_dir|$helper_dest"; do
        dir="${entry%%|*}"
        file="${entry#*|}"
        check_chain "$dir"
        check_target "$destdir$file"
        if [ -e "$destdir$file" ]; then
            rm -f -- "$destdir$file"
            echo "removed $file"
            removed=$((removed + 1))
        fi
    done
    if [ -d "$destdir$helper_dir" ] && rmdir "$destdir$helper_dir" 2>/dev/null; then
        echo "removed $helper_dir (it was empty)"
    fi
    if [ "$removed" -eq 0 ]; then echo "nothing to remove: Pennyworth's helper was not installed"; fi
    exit 0
fi

# --- sources -------------------------------------------------------------------------------------------------------
pick() { # candidate...
    local c
    for c in "$@"; do if [ -e "$c" ]; then echo "$c"; return 0; fi; done
    return 1
}
if [ -z "$helper_src" ]; then
    helper_src="$(pick "$here/libexec/pennyworth/pennyworth-helper" "$here/pennyworth-helper")" \
        || die "no helper found next to this script; pass the published binary with --helper PATH"
fi
if [ -z "$policy_src" ]; then
    policy_src="$(pick "$here/share/polkit-1/actions/app.pennyworth.policy" "$here/app.pennyworth.policy")" \
        || die "no policy found next to this script; pass it with --policy PATH"
fi

check_helper_file "$helper_src"
[ -f "$policy_src" ] || die "the policy $policy_src is not a file"
if command -v xmllint >/dev/null 2>&1; then
    xmllint --noout "$policy_src" || die "the policy $policy_src is not well-formed XML"
else
    echo "install-helper.sh: xmllint not found; the policy was not checked for well-formedness" >&2
fi
grep -qF ">$helper_dest<" "$policy_src" || die "the policy $policy_src does not name $helper_dest"

# --- destination ---------------------------------------------------------------------------------------------------
check_chain "$helper_dir"
check_chain "$policy_dir"
check_target "$destdir$helper_dest"
check_target "$destdir$policy_dest"

install -d "${owner_args[@]}" -m 0755 "$destdir$helper_dir" "$destdir$policy_dir"
# What was just created must satisfy the rule too (this also catches a folder swapped in meanwhile).
check_chain "$helper_dir"
check_chain "$policy_dir"

tmp=""
trap '[ -z "$tmp" ] || rm -f -- "$tmp"' EXIT

place() { # source destination mode
    local src="$1" dest="$2" mode="$3" before=""
    if [ -f "$dest" ] && cmp -s -- "$src" "$dest" \
        && [ "$(stat -c '%a' "$dest")" = "${mode#0}" ] \
        && { $test_mode || [ "$(stat -c '%u:%g' "$dest")" = 0:0 ]; }; then
        echo "unchanged ${dest#"$destdir"}"
        return
    fi
    [ -e "$dest" ] && before=replaced || before=installed
    tmp="$dest.new.$$"
    rm -f -- "$tmp"
    install "${owner_args[@]}" -m "$mode" -- "$src" "$tmp"
    if [ "$dest" = "$destdir$helper_dest" ]; then check_helper_file "$tmp"; fi   # the copy, not the file the caller could still change
    mv -f -- "$tmp" "$dest"
    tmp=""
    echo "$before ${dest#"$destdir"}"
}

place "$helper_src" "$destdir$helper_dest" 0755
place "$policy_src" "$destdir$policy_dest" 0644

cat <<EOF

Pennyworth's administrator helper is set up.
  It is asked for only when you press one of four buttons (memory modules, full system space,
  cleanup of system package caches and crash reports, size of folders your account cannot open).
  Your password is asked by the system; nothing is changed except a cleanup you confirm.
To remove it:  sudo $0 --uninstall
EOF
