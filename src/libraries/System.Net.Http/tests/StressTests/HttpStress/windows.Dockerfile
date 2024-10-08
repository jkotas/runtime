# escape=`
ARG SDK_BASE_IMAGE=mcr.microsoft.com/dotnet/nightly/sdk:8.0-nanoserver-ltsc2022
FROM $SDK_BASE_IMAGE

# Use powershell as the default shell
SHELL ["pwsh", "-Command"]

WORKDIR /app
COPY . .

ARG VERSION=9.0
ARG CONFIGURATION=Release

RUN dotnet build -c $env:CONFIGURATION `
    -p:NetCoreAppCurrentVersion=$env:VERSION `
    -p:MsQuicInteropIncludes="C:/live-runtime-artifacts/msquic-interop/*.cs" `
    -p:TargetingPacksTargetsLocation=C:/live-runtime-artifacts/targetingpacks.targets `
    -p:MicrosoftNetCoreAppRefPackDir=C:/live-runtime-artifacts/microsoft.netcore.app.ref/ `
    -p:MicrosoftNetCoreAppRuntimePackDir=C:/live-runtime-artifacts/microsoft.netcore.app.runtime.win-x64/$env:CONFIGURATION/

EXPOSE 5001

ENV VERSION=$VERSION
ENV CONFIGURATION=$CONFIGURATION
ENV STRESS_ROLE=''
ENV STRESS_ARGS=''

CMD ./entrypoint.ps1
