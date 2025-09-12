FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Install necessary packages
RUN apt-get update && apt-get install -y apt-utils libgdiplus libc6-dev

FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
ARG BUILD_CONFIGURATION=Release
ARG OCTO_PRIVATE_NUGET_SERVICE
ARG OCTO_PRIVATE_NUGET_CERTIFICATE
ARG OCTO_VERSION

COPY ${OCTO_PRIVATE_NUGET_CERTIFICATE} /usr/local/share/ca-certificates/nugetca.crt
RUN chmod 644 /usr/local/share/ca-certificates/nugetca.crt && update-ca-certificates

WORKDIR /src
COPY . .
ENV NUGET_XMLDOC_MODE=none
WORKDIR "/src/src/MeshAdapter"
RUN dotnet build "MeshAdapter.csproj" -c $BUILD_CONFIGURATION -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} -p:OctoVersion=${OCTO_VERSION} -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "MeshAdapter.csproj" -c $BUILD_CONFIGURATION -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} -p:OctoVersion=${OCTO_VERSION} -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.MeshAdapter.dll"]
