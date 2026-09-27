# CI image for NOCustomMaps, built on the Forgejo runner host (see docs/ci.md).
#
# Carries three things the workflows need and the stock .NET image does not:
#   - node, because actions/checkout is a JavaScript action
#   - zip, to package the release
#   - the Nuclear Option and BepInEx reference assemblies, which cannot be
#     committed to the repository
#
# This project references a superset of the assemblies NOAutoGCAS needs
# (AssetBundleModule, ImageConversionModule, UniTask, UI/UIModule, TextMeshPro),
# so the shared gcas-ci image must be rebuilt from this project's
# ci/collect-refs.ps1 output before its workflows will build.
#
# Build context must contain refs/Managed/ and refs/BepInEx/ as produced by
# ci/collect-refs.ps1.

FROM mcr.microsoft.com/dotnet/sdk:9.0

ARG NODE_MAJOR=20

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl gnupg zip \
 && mkdir -p /etc/apt/keyrings \
 && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
      | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
 && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_${NODE_MAJOR}.x nodistro main" \
      > /etc/apt/sources.list.d/nodesource.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends nodejs \
 && rm -rf /var/lib/apt/lists/*

# Reference assemblies. Read-only as far as the build is concerned; the
# publicizer writes its output into obj/ inside the workspace.
COPY refs/Managed/  /opt/nuclearoption/Managed/
COPY refs/BepInEx/  /opt/nuclearoption/BepInEx/

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

RUN node --version && dotnet --version
