# Independent staging QA component. Never reference this from production compose.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
FROM ${SDK_IMAGE} AS build
ARG CANDIDATE_SHA
WORKDIR /src
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.Config ./
COPY src/ContinuityBridge.Qa.Protocol/ src/ContinuityBridge.Qa.Protocol/
COPY src/ContinuityBridge.Qa.Sidecar/ src/ContinuityBridge.Qa.Sidecar/
RUN test "${#CANDIDATE_SHA}" -eq 40
RUN dotnet restore src/ContinuityBridge.Qa.Sidecar/ContinuityBridge.Qa.Sidecar.csproj --locked-mode
RUN dotnet publish src/ContinuityBridge.Qa.Sidecar/ContinuityBridge.Qa.Sidecar.csproj -c Release --no-restore -o /out -p:UseAppHost=false -p:SourceRevisionId=$CANDIDATE_SHA
FROM ${RUNTIME_IMAGE} AS final
ARG CANDIDATE_SHA
LABEL org.opencontainers.image.revision=$CANDIDATE_SHA
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Qa__LeaseFile=/run/secrets/lease.json
USER $APP_UID
ENTRYPOINT ["dotnet", "ContinuityBridge.Qa.Sidecar.dll"]
