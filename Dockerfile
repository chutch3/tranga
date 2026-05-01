# syntax=docker/dockerfile:1
ARG DOTNET=10.0
FROM mcr.microsoft.com/dotnet/aspnet:$DOTNET-alpine AS base

# Temporarily switch to root for Chromium install
USER root

RUN apk add --no-cache chromium krb5-libs

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:$DOTNET-alpine AS build-env
WORKDIR /src
COPY Tranga.sln /src
COPY API/API.csproj /src/API/API.csproj
RUN dotnet restore /src/API/API.csproj

COPY . /src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish /src/API/API.csproj -c Release --property:OutputPath=/publish -maxcpucount:1 --no-cache

FROM base AS runtime
WORKDIR /publish

# Expose port
EXPOSE 6531

# User setup
ARG UNAME=tranga
ARG UID=1000
ARG GID=1000
RUN addgroup -g $GID $UNAME \
  && adduser -D -u $UID -G $UNAME -s /bin/sh $UNAME \
  && mkdir /usr/share/tranga-api \
  && mkdir /Manga \
  && chown 1000:1000 /usr/share/tranga-api \
  && chown 1000:1000 /Manga

USER $UNAME

# Env vars for PuppeteerSharp (Chromium path + no-sandbox args)
ENV PUPPETEER_EXECUTABLE_PATH=/usr/bin/chromium-browser
ENV CHROME_BIN=/usr/bin/chromium-browser
ENV PUPPETEER_ARGS="--no-sandbox --disable-setuid-sandbox --disable-dev-shm-usage --disable-gpu --no-zygote --single-process"


WORKDIR /publish
COPY --chown=1000:1000 --from=build-env /publish .

# Root for entrypoint if needed
USER 0
ENTRYPOINT ["dotnet", "/publish/API.dll"]
CMD [""]
