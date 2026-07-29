FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/orchestrator/orchestrator.csproj", "orchestrator/"]
RUN dotnet restore "orchestrator/orchestrator.csproj"
COPY src/orchestrator/. ./orchestrator/
WORKDIR /src/orchestrator
RUN dotnet publish "orchestrator.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "orchestrator.dll"]
