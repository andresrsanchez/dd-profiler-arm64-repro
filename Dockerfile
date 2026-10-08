ARG DOTNET_RUNTIME=10.0.12
ARG TFM=net10.0
ARG DD_LIB_VERSION=3.55.0
ARG APM_INJECT_VERSION=0

FROM --platform=linux/arm64 gcr.io/datadoghq/dd-lib-dotnet-init:${DD_LIB_VERSION} AS ddlib
FROM --platform=linux/arm64 gcr.io/datadoghq/apm-inject:${APM_INJECT_VERSION} AS inject

FROM --platform=linux/arm64 mcr.microsoft.com/dotnet/sdk:10.0-alpine3.23 AS build
ARG TFM
WORKDIR /src
COPY src/Repro.csproj .
RUN dotnet restore -p:TargetFrameworks=${TFM}
COPY src/ .
RUN dotnet publish Repro.csproj -c Release -f ${TFM} -p:TargetFrameworks=${TFM} --no-restore -o /app

FROM --platform=linux/arm64 mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME}-alpine3.23
RUN apk add --no-cache icu-libs icu-data-full tzdata
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
COPY --from=inject /opt/datadog-packages /opt/datadog-packages
COPY --from=ddlib /datadog-init/package /opt/datadog/apm/library/dotnet
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Repro.dll"]
