# syntax=docker/dockerfile:1.7
# Muaallimi main backend — multi-stage build for local + prod parity.
#
# Build context: repo root (muallimi-main-backend/).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Muallimi.MainBackend.sln ./
COPY src/ ./src/
COPY tests/ ./tests/

RUN dotnet restore Muallimi.MainBackend.sln
RUN dotnet publish src/Muallimi.Api/Muallimi.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

EXPOSE 5063
ENV ASPNETCORE_URLS=http://+:5063

ENTRYPOINT ["dotnet", "Muallimi.Api.dll"]
