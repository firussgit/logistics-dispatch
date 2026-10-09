# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props LogisticsDispatch.slnx ./
COPY src/Core/*.csproj src/Core/
COPY src/Infrastructure/*.csproj src/Infrastructure/
COPY src/Api/*.csproj src/Api/
RUN dotnet restore src/Api/LogisticsDispatch.Api.csproj
COPY src/ src/
RUN dotnet publish src/Api/LogisticsDispatch.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The SQLite database lives on a volume so orders survive container restarts.
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Dispatch="Data Source=/data/dispatch.db"
RUN mkdir /data && chown $APP_UID /data
VOLUME /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "LogisticsDispatch.Api.dll"]
