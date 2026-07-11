# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Apiext.MediaKit.Client/Apiext.MediaKit.Client.csproj src/Apiext.MediaKit.Client/
COPY src/Apiext.MediaKit/Apiext.MediaKit.csproj src/Apiext.MediaKit/
RUN dotnet restore src/Apiext.MediaKit/Apiext.MediaKit.csproj
COPY src/ src/
RUN dotnet publish src/Apiext.MediaKit/Apiext.MediaKit.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5082
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5082
COPY --from=build /app/publish .
# Persist SQLite under /app/App_Data (mount a volume in compose).
RUN mkdir -p /app/App_Data
ENTRYPOINT ["dotnet", "Apiext.MediaKit.dll"]
