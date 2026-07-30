FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/database-migrator/database-migrator.csproj", "src/database-migrator/"]
COPY ["src/infrastructure/infrastructure.csproj", "src/infrastructure/"]
COPY ["src/application/application.csproj", "src/application/"]
COPY ["src/domain/domain.csproj", "src/domain/"]
RUN dotnet restore "src/database-migrator/database-migrator.csproj"
COPY . .
WORKDIR "/src/src/database-migrator"
RUN dotnet publish "database-migrator.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "database-migrator.dll"]
