FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Stockroom.Web/Stockroom.Web.csproj src/Stockroom.Web/packages.lock.json src/Stockroom.Web/
RUN dotnet restore src/Stockroom.Web/Stockroom.Web.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/Stockroom.Web/Stockroom.Web.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /out ./
RUN mkdir -p /app/App_Data /app/keys && chown -R $APP_UID /app/App_Data /app/keys
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Stockroom.Web.dll"]
