# Build from repository root. P3 must resolve/review image digests for its platform.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/ContinuityBridge.Contracts/ src/ContinuityBridge.Contracts/
COPY src/ContinuityBridge.Relay/ src/ContinuityBridge.Relay/
RUN dotnet restore src/ContinuityBridge.Relay/ContinuityBridge.Relay.csproj --locked-mode
RUN dotnet publish src/ContinuityBridge.Relay/ContinuityBridge.Relay.csproj -c Release --no-restore -o /out -p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS final
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Relay__DeviceFile=/run/secrets/devices.json
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ContinuityBridge.Relay.dll"]
