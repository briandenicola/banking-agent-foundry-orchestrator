FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/orchestrator/orchestrator.csproj", "src/orchestrator/"]
COPY ["src/application/application.csproj", "src/application/"]
COPY ["src/domain/domain.csproj", "src/domain/"]
COPY ["src/infrastructure/infrastructure.csproj", "src/infrastructure/"]
COPY ["src/api/api.csproj", "src/api/"]
RUN dotnet restore "src/orchestrator/orchestrator.csproj"
COPY . .
WORKDIR "/src/src/orchestrator"
RUN dotnet publish "orchestrator.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "orchestrator.dll"]
