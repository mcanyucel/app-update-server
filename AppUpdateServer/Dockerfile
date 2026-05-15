FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AppUpdateServer.slnx .
COPY AppUpdateServer/AppUpdateServer.csproj AppUpdateServer/
COPY AppUpdateServer.Cli/AppUpdateServer.Cli.csproj AppUpdateServer.Cli/
RUN dotnet restore AppUpdateServer/AppUpdateServer.csproj

COPY AppUpdateServer/ AppUpdateServer/
RUN dotnet publish AppUpdateServer/AppUpdateServer.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN mkdir -p /data/binaries

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "AppUpdateServer.dll"]
