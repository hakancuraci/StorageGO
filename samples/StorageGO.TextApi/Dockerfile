FROM mcr.microsoft.com/dotnet/sdk:8.0-noble AS build
WORKDIR /src

COPY samples/StorageGO.TextApi/StorageGO.TextApi.csproj ./
RUN dotnet restore StorageGO.TextApi.csproj \
    --source https://api.nuget.org/v3/index.json

COPY samples/StorageGO.TextApi/ ./
RUN dotnet publish StorageGO.TextApi.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "StorageGO.TextApi.dll"]
