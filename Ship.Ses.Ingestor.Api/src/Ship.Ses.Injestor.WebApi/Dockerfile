# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Ship.Ses.Ingestor.Api/src/Ship.Ses.Injestor.WebApi/Ship.Ses.Ingestor.Api.csproj", "Ship.Ses.Ingestor.Api/src/Ship.Ses.Injestor.WebApi/"]
COPY ["Ship.Ses.Ingestor.Api/src/Ship.Ses.Transmitter.Infrastructure/Ship.Ses.Transmitter.Infrastructure.csproj", "Ship.Ses.Ingestor.Api/src/Ship.Ses.Transmitter.Infrastructure/"]
COPY ["Ship.Ses.Ingestor.Api/src/Ship.Ses.Transmitter.Application/Ship.Ses.Transmitter.Application.csproj", "Ship.Ses.Ingestor.Api/src/Ship.Ses.Transmitter.Application/"]
COPY ["Ship.Ses.Ingestor.Api/src/Ship.Ses.Transmitter.Domain/Ship.Ses.Transmitter.Domain.csproj", "Ship.Ses.Ingestor.Api/src/Ship.Ses.Transmitter.Domain/"]
RUN dotnet restore "./Ship.Ses.Ingestor.Api/src/Ship.Ses.Injestor.WebApi/Ship.Ses.Ingestor.Api.csproj"
COPY . .
WORKDIR "/src/Ship.Ses.Ingestor.Api/src/Ship.Ses.Injestor.WebApi"
RUN dotnet build "./Ship.Ses.Ingestor.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Ship.Ses.Ingestor.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Ship.Ses.Ingestor.Api.dll"]