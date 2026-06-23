# Shared runtime base for AFH Sync worker + api.
#
# Holds the heavy, slow-changing layer: the ASP.NET 10 runtime + PowerShell +
# the ExchangeOnlineManagement module (DDGResolver needs pwsh to resolve DDG
# membership live during sync). This is the multi-minute, CPU+network-heavy step
# that previously ran on EVERY deploy in BOTH images and wedged the 2-core box.
#
# Build it ONCE on the box (or whenever you deliberately bump it):
#   docker build -t afh-runtime-base:1 base/
# Normal code deploys reuse this image and skip the PowerShell install entirely.
# deploy.sh auto-builds it if the image is missing.
#
# The upstream base is pinned BY DIGEST so a 10.0-noble retag upstream cannot
# silently invalidate this cached layer. To move to a newer base: refresh the
# digest, bump the tag to :2 here AND in worker/Dockerfile, api/Dockerfile, and
# BASE_IMAGE in deploy.sh, then rebuild.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:ddcf70ad1ab963a4fcd41fbd722a6b660e404e87567cfbd46fd2809c21b02088
RUN apt-get update && apt-get install -y --no-install-recommends \
    libkrb5-3 curl wget apt-transport-https && \
    wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb && \
    dpkg -i /tmp/packages-microsoft-prod.deb && rm /tmp/packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y --no-install-recommends powershell && \
    pwsh -c "Set-PSRepository PSGallery -InstallationPolicy Trusted; Install-Module ExchangeOnlineManagement -Force -AcceptLicense -Scope AllUsers" && \
    rm -rf /var/lib/apt/lists/*
