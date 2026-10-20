# Build environment for sandboxed machines.
#
# It keeps NuGet's global caches inside the repository instead of ~/.nuget, which
# matters when the home directory is read-only or when the build runs under a file
# sandbox. On a normal developer machine you can skip this file entirely.
#
#   source env.sh
#   dotnet publish src/SciBridge.App/SciBridge.App.csproj -c Release

_SCI_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

export NUGET_PACKAGES="$_SCI_ROOT/.build/nuget"
export NUGET_HTTP_CACHE_PATH="$_SCI_ROOT/.build/nuget-http"
export DOTNET_CLI_HOME="$_SCI_ROOT/.build/dotnet-home"
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
