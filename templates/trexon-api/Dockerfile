FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish TrexonApi.csproj -c Release -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
RUN mkdir -p /data /logs && chown -R $APP_UID:$APP_UID /data /logs
COPY --from=build /out .
ENV APP_HOST=0.0.0.0 APP_PORT=8080 APP_DATA_DIRECTORY=/data APP_LOG_DIRECTORY=/logs
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "TrexonApi.dll"]
