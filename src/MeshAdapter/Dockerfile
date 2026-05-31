FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443
# Target architecture of the image being built. Provided automatically by
# buildx / BuildKit (e.g. "amd64", "arm64"); falls back to the build host's
# dpkg architecture for a plain `docker build`.
ARG TARGETARCH

# Install necessary packages
RUN apt-get update && apt-get install -y \
    apt-utils \
    libgdiplus \
    libc6-dev \
    libtesseract5 \
    libtesseract-dev \
    libleptonica-dev \
    libgomp1 \
    wget \
    && rm -rf /var/lib/apt/lists/*

# Install libpng12 (legacy, not in modern Ubuntu; required by IronOCR's
# PdfOcrExtractionNode). It only ships for old Ubuntu releases and its dpkg
# *maintainer scripts* break on Noble (and on arm64 `dpkg -i` corrupts the live
# rootfs) — but the shipped libpng12.so itself is a perfectly good library on
# both arches. So we unpack the .deb into a temp dir with `dpkg-deb -x` and copy
# only the .so (+ its SONAME symlink) into the system lib dir, then ldconfig.
# Works identically on amd64 and arm64 and keeps OCR available on both. The .deb
# lives on archive.ubuntu.com for amd64 and ports.ubuntu.com for arm64.
RUN set -eux; \
    arch="${TARGETARCH:-$(dpkg --print-architecture)}"; \
    case "$arch" in \
      amd64) host="http://archive.ubuntu.com/ubuntu" ;; \
      *)     host="http://ports.ubuntu.com/ubuntu-ports" ;; \
    esac; \
    deb="libpng12-0_1.2.54-1ubuntu1_${arch}.deb"; \
    wget -q "${host}/pool/main/libp/libpng/${deb}"; \
    mkdir -p /tmp/libpng12; \
    dpkg-deb -x "${deb}" /tmp/libpng12; \
    for d in /tmp/libpng12/lib/*/; do triplet="$(basename "$d")"; done; \
    cp -a "/tmp/libpng12/lib/${triplet}/libpng12.so."* "/usr/lib/${triplet}/"; \
    /usr/sbin/ldconfig; \
    /usr/sbin/ldconfig -p | grep -q 'libpng12\.so\.0'; \
    rm -rf "${deb}" /tmp/libpng12

# Build on the native build platform (IL is portable) so multi-arch builds don't
# emulate the SDK; the runtime arch is selected by the `base`/`final` stages.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
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
