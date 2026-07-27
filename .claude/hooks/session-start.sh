#!/usr/bin/env bash
# Installs the .NET SDK Nmkoder targets into a Claude Code on the web container, so
# "dotnet build Nmkoder.sln -c Release" works from the first prompt of a session.
#
# The SDK comes from the Ubuntu archive rather than the usual dot.net installer script.
# That script redirects to builds.dotnet.microsoft.com, which the sandbox's egress proxy
# refuses with a 403, so the download fails before it starts. The archive is reachable
# and its dotnet-sdk-10.0 package carries the same SDK.
set -euo pipefail

# Only the web containers start out without .NET; leave local machines to their own install.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# Follow the project's own target framework, so a TFM bump does not leave this behind.
MAJOR="$(sed -n 's/.*<TargetFramework>net\([0-9][0-9]*\)\..*/\1/p' "$PROJECT_DIR/Nmkoder/Nmkoder.csproj" | head -1)"
MAJOR="${MAJOR:-10}"

# Containers run as root; keep working if some other environment does not. -E rides along
# with sudo so DEBIAN_FRONTEND survives into apt.
SUDO=""
[ "$(id -u)" -eq 0 ] || SUDO="sudo -E"

export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if dotnet --list-sdks 2>/dev/null | grep -q "^${MAJOR}\."; then
  echo "session-start: .NET ${MAJOR} SDK already present"
else
  echo "session-start: installing the .NET ${MAJOR} SDK"
  # The preloaded package index points at .debs the mirror has already superseded, so
  # every download 404s without this refresh. Blocked third-party PPAs only warn.
  $SUDO apt-get update -qq
  $SUDO apt-get install -y "dotnet-sdk-${MAJOR}.0"
fi

# Restore now so the packages land in the container's NuGet cache while it is still being
# built, rather than on the first build of every session.
dotnet restore "$PROJECT_DIR/Nmkoder.sln"

# Keep the build quiet for the rest of the session; the release workflow opts out of
# Avalonia's telemetry the same way. SessionStart also fires on resume, clear and compact,
# so only add what is not in the file already.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  for var in DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_NOLOGO AVALONIA_TELEMETRY_OPTOUT; do
    grep -q "^export ${var}=" "$CLAUDE_ENV_FILE" 2>/dev/null && continue
    echo "export ${var}=1" >> "$CLAUDE_ENV_FILE"
  done
fi

echo "session-start: .NET SDK $(dotnet --version) ready"
